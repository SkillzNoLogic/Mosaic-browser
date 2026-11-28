using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Mosaic.Core.Platform;

/// <summary>What Windows says about a file's Authenticode signature.</summary>
public enum AuthenticodeStatus
{
    /// <summary>Signed, intact, and the chain leads to a root Windows trusts.</summary>
    Valid,
    /// <summary>No embedded signature.</summary>
    NotSigned,
    /// <summary>Signed, but the file was changed after signing, or the signature or chain is unacceptable.</summary>
    Invalid,
    /// <summary>This system cannot check signatures (not Windows).</summary>
    Unsupported,
}

/// <param name="Status">The verdict of <c>WinVerifyTrust</c>.</param>
/// <param name="Detail">Windows' error code and meaning when not valid.</param>
/// <param name="SignerSubject">Distinguished name of the signing certificate, when there is one.</param>
/// <param name="SignerOrganisation">Its organisation (O=) attribute.</param>
public sealed record AuthenticodeResult(AuthenticodeStatus Status, string Detail = "", string? SignerSubject = null, string? SignerOrganisation = null);

/// <summary>Checks a file's Authenticode signature. The app uses <see cref="WinTrustVerifier"/>; tests use fakes.</summary>
public interface IAuthenticodeVerifier
{
    /// <summary>Never throws; blocking (call off the UI thread).</summary>
    AuthenticodeResult Verify(string path);
}

/// <summary>
/// Who must have signed the Gecko runtime Mosaic installs. Windows counterpart of
/// <c>SignerExpectation</c> in <c>CodeSignatureCheck.swift</c>: the same rule
/// <c>windows/scripts/bootstrap.ps1</c> applies (status Valid and the signer's O= is Mozilla Corporation).
/// </summary>
public sealed record SignerExpectation(string Organisation)
{
    /// <summary>Mozilla's code-signing identity on Firefox's Windows installers and executables.</summary>
    public static readonly SignerExpectation MozillaFirefox = new("Mozilla Corporation");

    public SignatureVerdict Evaluate(AuthenticodeResult result) => result.Status switch
    {
        AuthenticodeStatus.Valid when string.Equals(result.SignerOrganisation, Organisation, StringComparison.Ordinal) => new SignatureVerdict.Trusted(result),
        AuthenticodeStatus.Valid => new SignatureVerdict.WrongSigner(result.SignerSubject),
        AuthenticodeStatus.NotSigned => new SignatureVerdict.WrongSigner(null),
        AuthenticodeStatus.Unsupported => new SignatureVerdict.Unsupported(),
        _ => new SignatureVerdict.Invalid(result.Detail),
    };
}

public abstract record SignatureVerdict
{
    /// <summary>Intact and signed by the expected publisher.</summary>
    public sealed record Trusted(AuthenticodeResult Result) : SignatureVerdict;
    /// <summary>Modified after signing, damaged, or an untrusted chain.</summary>
    public sealed record Invalid(string Detail) : SignatureVerdict;
    /// <summary>Intact, but signed by somebody else (or not at all).</summary>
    public sealed record WrongSigner(string? Found) : SignatureVerdict;
    public sealed record Unsupported : SignatureVerdict;

    /// <summary>The installer failure this verdict means, or null when trusted.</summary>
    public RuntimeInstallFailure? AsFailure() => this switch
    {
        Trusted => null,
        Invalid invalid => new RuntimeInstallFailure.SignatureInvalid(invalid.Detail),
        WrongSigner wrong => new RuntimeInstallFailure.WrongSigner(wrong.Found),
        _ => new RuntimeInstallFailure.Unsupported(),
    };
}

/// <summary>
/// <see cref="IAuthenticodeVerifier"/> on Windows' own <c>WinVerifyTrust</c>
/// (<c>WINTRUST_ACTION_GENERIC_VERIFY_V2</c>, the policy <c>Get-AuthenticodeSignature</c> and
/// Explorer use), reading the signer's certificate from the same verification, so the name that is
/// checked belongs to the signature that was verified. Revocation is not checked online (as with
/// <c>Get-AuthenticodeSignature</c>): an install must not hang on an unreachable CRL server.
/// </summary>
public sealed class WinTrustVerifier : IAuthenticodeVerifier
{
    public AuthenticodeResult Verify(string path) =>
        OperatingSystem.IsWindows() ? VerifyOnWindows(path) : new AuthenticodeResult(AuthenticodeStatus.Unsupported, "not Windows");

    [SupportedOSPlatform("windows")]
    private static AuthenticodeResult VerifyOnWindows(string path)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");  // WINTRUST_ACTION_GENERIC_VERIFY_V2
        var filePath = Marshal.StringToHGlobalUni(Path.GetFullPath(path));
        var fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        var data = new WintrustData();
        try
        {
            Marshal.StructureToPtr(new WintrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
                pcwszFilePath = filePath,
            }, fileInfo, false);
            data.cbStruct = (uint)Marshal.SizeOf<WintrustData>();
            data.dwUIChoice = WtdUiNone;
            data.fdwRevocationChecks = WtdRevokeNone;
            data.dwUnionChoice = WtdChoiceFile;
            data.pFile = fileInfo;
            data.dwStateAction = WtdStateActionVerify;
            data.dwProvFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval;

            var status = WinVerifyTrust(-1, ref action, ref data);
            try
            {
                var (subject, organisation) = SignerOf(data.hWVTStateData);
                return status switch
                {
                    0 => new AuthenticodeResult(AuthenticodeStatus.Valid, "", subject, organisation),
                    TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown =>
                        new AuthenticodeResult(AuthenticodeStatus.NotSigned, Describe(status), subject, organisation),
                    _ => new AuthenticodeResult(AuthenticodeStatus.Invalid, Describe(status), subject, organisation),
                };
            }
            finally
            {
                data.dwStateAction = WtdStateActionClose;
                WinVerifyTrust(-1, ref action, ref data);
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return new AuthenticodeResult(AuthenticodeStatus.Invalid, error.Message);
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfo);
            Marshal.FreeHGlobal(filePath);
        }
    }

    /// <summary>The leaf certificate of the primary signer, from the verification's provider data.</summary>
    [SupportedOSPlatform("windows")]
    private static (string? Subject, string? Organisation) SignerOf(nint state)
    {
        if (state == 0) return (null, null);
        var provider = WTHelperProvDataFromStateData(state);
        if (provider == 0) return (null, null);
        var signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signer == 0) return (null, null);
        // CRYPT_PROVIDER_SGNR: DWORD cbStruct; FILETIME sftVerifyAsOf; DWORD csCertChain;
        // CRYPT_PROVIDER_CERT *pasCertChain; … (the pointer is 8-aligned on 64-bit Windows).
        var chainCount = Marshal.ReadInt32(signer, 12);
        var chain = Marshal.ReadIntPtr(signer, nint.Size == 8 ? 16 : 16);
        if (chainCount < 1 || chain == 0) return (null, null);
        // CRYPT_PROVIDER_CERT: DWORD cbStruct; PCCERT_CONTEXT pCert; …
        var context = Marshal.ReadIntPtr(chain, nint.Size);
        if (context == 0) return (null, null);
        try
        {
            using var certificate = new X509Certificate2(context);
            return (certificate.Subject, OrganisationOf(certificate.SubjectName));
        }
        catch (CryptographicException)
        {
            return (null, null);
        }
    }

    /// <summary>The O= attribute of a distinguished name.</summary>
    public static string? OrganisationOf(X500DistinguishedName name)
    {
        foreach (var component in name.EnumerateRelativeDistinguishedNames())
        {
            if (component.GetSingleElementType().Value == "2.5.4.10") return component.GetSingleElementValue();
        }
        return null;
    }

    private static string Describe(int status) => status switch
    {
        TrustENoSignature => "no signature (TRUST_E_NOSIGNATURE)",
        TrustESubjectFormUnknown => "not a signable file (TRUST_E_SUBJECT_FORM_UNKNOWN)",
        TrustEProviderUnknown => "no signature provider (TRUST_E_PROVIDER_UNKNOWN)",
        unchecked((int)0x80096010) => "the file was changed after it was signed (TRUST_E_BAD_DIGEST)",
        unchecked((int)0x800B0109) => "the certificate chain ends in an untrusted root (CERT_E_UNTRUSTEDROOT)",
        unchecked((int)0x800B0101) => "the signing certificate has expired (CERT_E_EXPIRED)",
        unchecked((int)0x800B010C) => "the signing certificate was revoked (CERT_E_REVOKED)",
        unchecked((int)0x800B0111) => "the publisher is explicitly distrusted (TRUST_E_EXPLICIT_DISTRUST)",
        unchecked((int)0x80096019) => "the signing certificate is not valid for code signing (TRUST_E_BASIC_CONSTRAINTS)",
        _ => $"WinVerifyTrust error 0x{status:X8}",
    };

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x10;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public nint pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public nint pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public nint pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(nint hwnd, ref Guid pgActionID, ref WintrustData pWVTData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperProvDataFromStateData(nint hStateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvSignerFromChain(
        nint pProvData, uint idxSigner, [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, uint idxCounterSigner);
}

/// <summary>Reads the target machine of a Windows executable (its PE header).</summary>
public static class PeImage
{
    public const ushort MachineX64 = 0x8664;
    public const ushort MachineArm64 = 0xAA64;

    /// <summary>The <c>IMAGE_FILE_HEADER.Machine</c> of <paramref name="path"/>, or null if it is not a PE file.</summary>
    public static ushort? Machine(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) return null;  // "MZ"
            stream.Position = 0x3C;
            var header = reader.ReadInt32();
            if (header <= 0 || header > stream.Length - 6) return null;
            stream.Position = header;
            if (reader.ReadUInt32() != 0x00004550) return null;  // "PE\0\0"
            return reader.ReadUInt16();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static ushort MachineFor(MachineArchitecture architecture) =>
        architecture == MachineArchitecture.Arm64 ? MachineArm64 : MachineX64;

    /// <summary>
    /// Whether a program built for <paramref name="machine"/> runs natively on
    /// <paramref name="architecture"/>. ARM64X binaries report the x64 machine in their header but
    /// carry native ARM64 code too; Firefox's ARM64 builds are plain ARM64.
    /// </summary>
    public static bool IsNative(ushort? machine, MachineArchitecture architecture) => machine == MachineFor(architecture);
}

using Org.BouncyCastle.Math.EC.Rfc8032;
using Org.BouncyCastle.Security;

namespace Mosaic.Core.Tests.Platform;

/// <summary>
/// A throwaway Ed25519 key for tests, standing in for Mosaic's release key (whose private half
/// exists only in the release machine's Keychain and never anywhere near the tests).
/// </summary>
internal sealed class TestSigningKey
{
    private readonly byte[] secret = new byte[Ed25519.SecretKeySize];
    private readonly byte[] publicKey = new byte[Ed25519.PublicKeySize];

    public TestSigningKey()
    {
        Ed25519.GeneratePrivateKey(new SecureRandom(), secret);
        Ed25519.GeneratePublicKey(secret, 0, publicKey, 0);
    }

    public string PublicKey => Convert.ToBase64String(publicKey);

    /// <summary>The <c>.sig</c> file content Scripts/sign-update.swift writes: base64 and a newline.</summary>
    public string Sign(byte[] data)
    {
        var signature = new byte[Ed25519.SignatureSize];
        Ed25519.Sign(secret, 0, data, 0, data.Length, signature, 0);
        return Convert.ToBase64String(signature) + "\n";
    }

    public string SignFile(string path) => Sign(File.ReadAllBytes(path));
}

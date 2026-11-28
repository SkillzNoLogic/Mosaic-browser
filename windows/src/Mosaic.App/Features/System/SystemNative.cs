using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Mosaic.App.Features.System;

// Win32 and shell COM interfaces used by the system area. Source-generated COM
// ([GeneratedComInterface]) so it keeps working if the app is ever trimmed or AOT-compiled.
// Interfaces list their vtable in order; trailing methods Mosaic never calls are omitted.

internal static partial class SystemNative
{
    public const uint CLSCTX_INPROC_SERVER = 0x1;
    public const uint CLSCTX_LOCAL_SERVER = 0x4;

    public static readonly Guid CLSID_TaskbarList = new("56FDF344-FD6D-11d0-958A-006097C9A090");
    public static readonly Guid CLSID_DestinationList = new("77f10cf0-3db5-4966-b520-b7c54fd35ed6");
    public static readonly Guid CLSID_EnumerableObjectCollection = new("2d3468c1-36a7-43b6-ac24-d3f02fd9607a");
    public static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
    public static readonly Guid CLSID_VirtualDesktopManager = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");

    public static readonly Guid IID_IObjectArray = new("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9");
    public static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    /// <summary>PKEY_Title: a jump-list task's visible title.</summary>
    public static readonly PropertyKey PKEY_Title = new(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);

    public const ushort VT_LPWSTR = 31;

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    [LibraryImport("shell32.dll")]
    public static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

    public const int SHCNE_ASSOCCHANGED = 0x08000000;
    public const uint SHCNF_IDLIST = 0x0000;

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int AssocQueryStringW(uint flags, int str, string assoc, string? extra, [Out] char[]? output, ref uint length);

    public const uint ASSOCF_IS_PROTOCOL = 0x00001000;
    public const int ASSOCSTR_PROGID = 20;

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint window, uint flags);

    public const uint GA_ROOT = 2;

    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint window, uint command);

    public const uint GW_OWNER = 4;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint window);

    /// <summary>Creates a COM object and wraps it as <typeparamref name="T"/>; null when it is unavailable.</summary>
    public static T? Create<T>(Guid clsid, uint context = CLSCTX_INPROC_SERVER) where T : class
    {
        var iid = typeof(T).GUID;
        if (CoCreateInstance(clsid, 0, context, iid, out var pointer) < 0 || pointer == 0) return null;
        try
        {
            return (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    public static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>The raw interface pointer (AddRef'ed) for passing an object to another COM method.</summary>
    public static unsafe nint PointerFor<T>(T instance) where T : class =>
        (nint)ComInterfaceMarshaller<T>.ConvertToUnmanaged(instance);
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct PropertyKey(Guid formatId, uint propertyId)
{
    public readonly Guid FormatId = formatId;
    public readonly uint PropertyId = propertyId;
}

/// <summary>A PROPVARIANT holding a string (VT_LPWSTR). 24 bytes on 64-bit Windows.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort Type;
    private ushort reserved1, reserved2, reserved3;
    public nint Pointer;
    private nint padding;

    public static PropVariant FromString(string text) => new() { Type = SystemNative.VT_LPWSTR, Pointer = Marshal.StringToCoTaskMemUni(text) };

    public void Free()
    {
        if (Pointer != 0) Marshal.FreeCoTaskMem(Pointer);
        Pointer = 0;
    }
}

internal enum TaskbarFlags
{
    NoProgress = 0,
    Indeterminate = 0x1,
    Normal = 0x2,
    Error = 0x4,
    Paused = 0x8,
}

/// <summary>ITaskbarList3 (with ITaskbarList and ITaskbarList2's methods first), up to SetProgressState.</summary>
[GeneratedComInterface]
[Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
internal partial interface ITaskbarList3
{
    [PreserveSig] int HrInit();
    [PreserveSig] int AddTab(nint window);
    [PreserveSig] int DeleteTab(nint window);
    [PreserveSig] int ActivateTab(nint window);
    [PreserveSig] int SetActiveAlt(nint window);
    [PreserveSig] int MarkFullscreenWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
    [PreserveSig] int SetProgressValue(nint window, ulong completed, ulong total);
    [PreserveSig] int SetProgressState(nint window, TaskbarFlags flags);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("6332debf-87b5-4670-90c0-5e57b408a49e")]
internal partial interface ICustomDestinationList
{
    [PreserveSig] int SetAppID(string appId);
    [PreserveSig] int BeginList(out uint minSlots, in Guid iid, out nint removed);
    [PreserveSig] int AppendCategory(string category, nint objectArray);
    [PreserveSig] int AppendKnownCategory(int category);
    [PreserveSig] int AddUserTasks(nint objectArray);
    [PreserveSig] int CommitList();
    [PreserveSig] int GetRemovedDestinations(in Guid iid, out nint removed);
    [PreserveSig] int DeleteList(string? appId);
    [PreserveSig] int AbortList();
}

/// <summary>IObjectCollection (IObjectArray's two methods first).</summary>
[GeneratedComInterface]
[Guid("5632b1a4-e38a-400a-928a-d4cd63230295")]
internal partial interface IObjectCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, in Guid iid, out nint item);
    [PreserveSig] int AddObject(nint item);
    [PreserveSig] int AddFromArray(nint source);
    [PreserveSig] int RemoveObjectAt(uint index);
    [PreserveSig] int Clear();
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal partial interface IShellLinkW
{
    [PreserveSig] int GetPath(nint file, int length, nint findData, uint flags);
    [PreserveSig] int GetIDList(out nint idList);
    [PreserveSig] int SetIDList(nint idList);
    [PreserveSig] int GetDescription(nint name, int length);
    [PreserveSig] int SetDescription(string name);
    [PreserveSig] int GetWorkingDirectory(nint directory, int length);
    [PreserveSig] int SetWorkingDirectory(string directory);
    [PreserveSig] int GetArguments(nint arguments, int length);
    [PreserveSig] int SetArguments(string arguments);
    [PreserveSig] int GetHotkey(out ushort hotkey);
    [PreserveSig] int SetHotkey(ushort hotkey);
    [PreserveSig] int GetShowCmd(out int showCommand);
    [PreserveSig] int SetShowCmd(int showCommand);
    [PreserveSig] int GetIconLocation(nint path, int length, out int index);
    [PreserveSig] int SetIconLocation(string path, int index);
    [PreserveSig] int SetRelativePath(string path, uint reserved);
    [PreserveSig] int Resolve(nint window, uint flags);
    [PreserveSig] int SetPath(string file);
}

[GeneratedComInterface]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal partial interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(in PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(in PropertyKey key, in PropVariant value);
    [PreserveSig] int Commit();
}

[GeneratedComInterface]
[Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
internal partial interface IVirtualDesktopManager
{
    [PreserveSig] int IsWindowOnCurrentVirtualDesktop(nint window, [MarshalAs(UnmanagedType.Bool)] out bool onCurrent);
    [PreserveSig] int GetWindowDesktopId(nint window, out Guid desktopId);
    [PreserveSig] int MoveWindowToDesktop(nint window, in Guid desktopId);
}

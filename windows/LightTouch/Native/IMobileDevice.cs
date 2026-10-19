// libimobiledevice-1.0.dll and libplist-2.0.dll (windows/scripts/build-usb.sh): the calls the services make, as the
// Mac's LightTouchServices links them. Every call reaches the device through the device's own usbmuxd
// (USBMUXD_SOCKET_ADDRESS, UsbMux). Strings the libraries allocate are freed with msvcrt's free, plists' text with
// plist_mem_free.

using System.Runtime.InteropServices;

namespace LightTouch.Native;

internal static unsafe partial class IMobileDevice
{
    private const string Library = "libimobiledevice-1.0.dll";
    private const string Plist = "libplist-2.0.dll";

    public const int AfcReadOnly = 1, AfcWriteOnly = 3;
    public const int AfcObjectNotFound = 8;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void StatusCallback(IntPtr command, IntPtr status, IntPtr userData);

    // MARK: - Device and lockdown

    [LibraryImport(Library, EntryPoint = "idevice_new", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int DeviceNew(out IntPtr device, string? udid);

    [LibraryImport(Library, EntryPoint = "idevice_free")]
    public static partial int DeviceFree(IntPtr device);

    [LibraryImport(Library, EntryPoint = "lockdownd_client_new_with_handshake", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int LockdownNew(IntPtr device, out IntPtr client, string label);

    [LibraryImport(Library, EntryPoint = "lockdownd_client_free")]
    public static partial int LockdownFree(IntPtr client);

    [LibraryImport(Library, EntryPoint = "lockdownd_get_value", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int LockdownGetValue(IntPtr client, string? domain, string? key, out IntPtr value);

    [LibraryImport(Library, EntryPoint = "lockdownd_start_service", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int LockdownStartService(IntPtr client, string identifier, out IntPtr descriptor);

    [LibraryImport(Library, EntryPoint = "lockdownd_service_descriptor_free")]
    public static partial int ServiceDescriptorFree(IntPtr descriptor);

    // MARK: - installation_proxy

    [LibraryImport(Library, EntryPoint = "instproxy_client_new")]
    public static partial int InstproxyNew(IntPtr device, IntPtr descriptor, out IntPtr client);

    [LibraryImport(Library, EntryPoint = "instproxy_client_free")]
    public static partial int InstproxyFree(IntPtr client);

    [LibraryImport(Library, EntryPoint = "instproxy_browse")]
    public static partial int InstproxyBrowse(IntPtr client, IntPtr options, out IntPtr result);

    [LibraryImport(Library, EntryPoint = "instproxy_lookup_archives")]
    public static partial int InstproxyLookupArchives(IntPtr client, IntPtr options, out IntPtr result);

    [LibraryImport(Library, EntryPoint = "instproxy_install", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int InstproxyInstall(IntPtr client, string path, IntPtr options, IntPtr callback, IntPtr userData);

    [LibraryImport(Library, EntryPoint = "instproxy_archive", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int InstproxyArchive(IntPtr client, string appID, IntPtr options, IntPtr callback, IntPtr userData);

    [LibraryImport(Library, EntryPoint = "instproxy_restore", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int InstproxyRestore(IntPtr client, string appID, IntPtr options, IntPtr callback, IntPtr userData);

    [LibraryImport(Library, EntryPoint = "instproxy_uninstall", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int InstproxyUninstall(IntPtr client, string appID, IntPtr options, IntPtr callback, IntPtr userData);

    [LibraryImport(Library, EntryPoint = "instproxy_status_get_error")]
    public static partial int StatusGetError(IntPtr status, out IntPtr name, out IntPtr description, out ulong code);

    [LibraryImport(Library, EntryPoint = "instproxy_status_get_name")]
    public static partial void StatusGetName(IntPtr status, out IntPtr name);

    [LibraryImport(Library, EntryPoint = "instproxy_status_get_percent_complete")]
    public static partial void StatusGetPercent(IntPtr status, out int percent);

    // MARK: - AFC

    [LibraryImport(Library, EntryPoint = "afc_client_new")]
    public static partial int AfcNew(IntPtr device, IntPtr descriptor, out IntPtr client);

    [LibraryImport(Library, EntryPoint = "afc_client_free")]
    public static partial int AfcFree(IntPtr client);

    [LibraryImport(Library, EntryPoint = "afc_file_open", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int AfcOpen(IntPtr client, string path, int mode, out ulong handle);

    [LibraryImport(Library, EntryPoint = "afc_file_close")]
    public static partial int AfcClose(IntPtr client, ulong handle);

    [LibraryImport(Library, EntryPoint = "afc_file_write")]
    public static partial int AfcWrite(IntPtr client, ulong handle, byte* data, uint length, out uint written);

    [LibraryImport(Library, EntryPoint = "afc_file_read")]
    public static partial int AfcRead(IntPtr client, ulong handle, byte* data, uint length, out uint read);

    [LibraryImport(Library, EntryPoint = "afc_make_directory", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int AfcMakeDirectory(IntPtr client, string path);

    [LibraryImport(Library, EntryPoint = "afc_remove_path", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int AfcRemove(IntPtr client, string path);

    [LibraryImport(Library, EntryPoint = "afc_rename_path", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int AfcRename(IntPtr client, string from, string to);

    [LibraryImport(Library, EntryPoint = "afc_get_device_info_key", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int AfcDeviceInfoKey(IntPtr client, string key, out IntPtr value);

    [LibraryImport(Library, EntryPoint = "afc_read_directory", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int AfcReadDirectory(IntPtr client, string path, out IntPtr list);

    [LibraryImport(Library, EntryPoint = "afc_dictionary_free")]
    public static partial int AfcDictionaryFree(IntPtr list);

    // MARK: - libplist

    [LibraryImport(Plist, EntryPoint = "plist_from_xml")]
    private static partial int PlistFromXml(byte* xml, uint length, out IntPtr plist);

    [LibraryImport(Plist, EntryPoint = "plist_from_memory")]
    private static partial int PlistFromMemory(byte* data, uint length, out IntPtr plist, IntPtr format);

    [LibraryImport(Plist, EntryPoint = "plist_to_xml")]
    private static partial int PlistToXml(IntPtr plist, out IntPtr xml, out uint length);

    [LibraryImport(Plist, EntryPoint = "plist_free")]
    public static partial void PlistFree(IntPtr plist);

    [LibraryImport(Plist, EntryPoint = "plist_mem_free")]
    private static partial void PlistMemFree(IntPtr memory);

    [LibraryImport("msvcrt.dll", EntryPoint = "free")]
    private static partial void CrtFree(IntPtr memory);

    /// A plist node for a value (Plist.Write's types); free it with PlistFree.
    public static IntPtr ToPlist(object value)
    {
        var xml = Native.Plist.Write(value);
        fixed (byte* p = xml)
        {
            return PlistFromXml(p, (uint)xml.Length, out var plist) == 0 ? plist : IntPtr.Zero;
        }
    }

    /// A plist node's value (Plist.Read's types); the node stays the caller's.
    public static object? FromPlist(IntPtr plist)
    {
        if (plist == IntPtr.Zero || PlistToXml(plist, out var xml, out var length) != 0) return null;
        try { return Native.Plist.Read(new ReadOnlySpan<byte>((void*)xml, (int)length)); }
        finally { PlistMemFree(xml); }
    }

    /// Any plist file's bytes (binary or XML, as an .ipa's Info.plist may be either), decoded.
    public static object? ParsePlist(byte[] data)
    {
        fixed (byte* p = data)
        {
            if (PlistFromMemory(p, (uint)data.Length, out var plist, IntPtr.Zero) != 0) return null;
            try { return FromPlist(plist); }
            finally { PlistFree(plist); }
        }
    }

    /// A C string the library allocated, taken and freed.
    public static string? TakeString(IntPtr value)
    {
        if (value == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(value); }
        finally { CrtFree(value); }
    }

    /// A NULL-terminated char** AFC list, taken and freed.
    public static List<string> TakeList(IntPtr list)
    {
        var result = new List<string>();
        if (list == IntPtr.Zero) return result;
        for (var p = (IntPtr*)list; *p != IntPtr.Zero; p++) result.Add(Marshal.PtrToStringUTF8(*p) ?? "");
        AfcDictionaryFree(list);
        return result;
    }
}

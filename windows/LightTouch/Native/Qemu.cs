// qemu-arm.dll: the qemu_ios_* ABI (qemu-ios contrib/ios-app/qemu-ios-ui.h and contrib/macos-app/qemu-macos-extras.h),
// the same entry points LightTouchDevice's Qemu.swift loads from libqemu-arm.dylib. Every call but Main is safe from
// any thread: the emulator marshals each onto its own thread.

using System.Runtime.InteropServices;
using System.Text;

namespace LightTouch.Native;

internal static unsafe partial class Qemu
{
    private const string Library = "qemu-arm.dll";

    /// The ABI major this app is written against; another major's DLL is refused (Qemu.apiMajor on the Mac).
    public const uint ApiMajor = 2;

    public const int TouchBegin = 0, TouchUpdate = 1, TouchEnd = 2;
    public const int ButtonHome = 0, ButtonPower = 1, ButtonVolumeUp = 2, ButtonVolumeDown = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfo
    {
        public IntPtr Machine;
        public IntPtr Board;
        public int ScreenWidth, ScreenHeight, ScreenScale, DefaultOrientation;
        [MarshalAs(UnmanagedType.U1)] public bool HasCellular;
        [MarshalAs(UnmanagedType.U1)] public bool HasUsbHost;
        [MarshalAs(UnmanagedType.U1)] public bool HasCompass;
        [MarshalAs(UnmanagedType.U1)] public bool HasUsbCharger;
        public int PanelMin, PanelMaxWidth, PanelMaxHeight, PanelWidthStep, PanelMaxPixels;

        public readonly string MachineName => Marshal.PtrToStringUTF8(Machine) ?? "";
        public readonly string BoardName => Marshal.PtrToStringUTF8(Board) ?? "";
    }

    [LibraryImport(Library, EntryPoint = "qemu_ios_main")]
    private static partial int MainNative(int argc, IntPtr* argv);

    /// Runs the emulator on the calling thread until the VM stops; once per process.
    public static int Run(IReadOnlyList<string> argv)
    {
        var strings = new IntPtr[argv.Count + 1];
        for (var i = 0; i < argv.Count; i++) strings[i] = Marshal.StringToCoTaskMemUTF8(argv[i]);
        // Never freed: QEMU keeps pointers into argv for the process's life.
        fixed (IntPtr* p = strings) return MainNative(argv.Count, p);
    }

    [LibraryImport(Library, EntryPoint = "qemu_ios_device_info_at")]
    private static partial DeviceInfo* DeviceInfoAt(int index);

    /// Every machine this DLL runs, with the emulator's facts about it (DeviceInfo.list on the Mac).
    public static List<DeviceInfo> Machines()
    {
        var list = new List<DeviceInfo>();
        for (var i = 0; ; i++)
        {
            var info = DeviceInfoAt(i);
            if (info == null) break;
            list.Add(*info);
        }
        return list;
    }

    [LibraryImport(Library, EntryPoint = "qemu_ios_api_version")]
    public static partial uint ApiVersion();

    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_attach")]
    public static partial void Attach(IntPtr callback, IntPtr opaque);

    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_ready")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool Ready();

    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_storage_failed")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool StorageFailed();

    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_guest_shutdown_confirmed")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GuestShutdownConfirmed();

    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_display_sleeping")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool DisplaySleeping();

    /// The newest frame, tightly packed BGRA, without a copy; false when nothing is newer than `serial`.
    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_frame")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool Frame(out IntPtr pixels, out int width, out int height, ref ulong serial);

    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_touch")]
    public static partial void Touch(int slot, int phase, double x, double y);

    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_touch2")]
    public static partial void Touch2(int phase, double x, double y);

    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_button")]
    public static partial void Button(int button, [MarshalAs(UnmanagedType.U1)] bool down);

    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_key_mac")]
    public static partial void KeyMac(int macKeyCode, [MarshalAs(UnmanagedType.U1)] bool down);

    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_rotate")]
    public static partial void Rotate([MarshalAs(UnmanagedType.U1)] bool clockwise);

    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_shake")]
    public static partial void Shake();

    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_paste", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void Paste(string text);

    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_guest_pasteboard")]
    private static partial IntPtr GuestPasteboardNative(ref ulong serial);

    [LibraryImport("msvcrt.dll", EntryPoint = "free")]
    private static partial void CrtFree(IntPtr pointer);

    /// The text last copied inside the guest, when it copied since `serial`; null otherwise.
    public static string? GuestPasteboard(ref ulong serial)
    {
        var text = GuestPasteboardNative(ref serial);
        if (text == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(text); }
        finally { CrtFree(text); }
    }

    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_pause")]
    public static partial void Pause();

    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_resume")]
    public static partial void Resume();

    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_reset")]
    public static partial void Reset();

    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_quit")]
    public static partial void Quit();

    /// 1: the guest agent was asked to halt; 2: the board's power-off gesture; 0: nothing started.
    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_shutdown")]
    public static partial int Shutdown();

    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_net_restrict", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void NetRestrict(string id, [MarshalAs(UnmanagedType.U1)] bool restricted);

    [LibraryImport(Library, EntryPoint = "qemu_ios_ui_hardware_keyboard")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool HardwareKeyboard([MarshalAs(UnmanagedType.U1)] bool attached);

    [LibraryImport("msvcrt.dll", EntryPoint = "_set_fmode")]
    private static partial int SetFileMode(int mode);

    /// Makes the C runtime open files in binary mode unless told otherwise, as on POSIX: the emulator's own open() and
    /// fopen() calls name no mode, and a text-mode read stops at the first 0x1A (a GID blob file has one).
    public static void UseBinaryFiles() => SetFileMode(0x8000);

    [LibraryImport("msvcrt.dll", EntryPoint = "_putenv_s", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int PutEnv(string name, string value);

    /// Sets a variable the emulator reads with getenv(): msvcrt keeps its own copy of the environment, which
    /// Environment.SetEnvironmentVariable doesn't reach once msvcrt.dll is loaded.
    public static void SetEnvironment(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        PutEnv(name, value);
    }

    /// The DLL's ABI version as text ("2.3"), for the log.
    public static string ApiVersionText()
    {
        var v = ApiVersion();
        return new StringBuilder().Append(v >> 16).Append('.').Append(v & 0xFFFF).ToString();
    }
}

// The device's usbmuxd (the fork's QEMU backend, windows/scripts/build-usb.sh), as the Mac's USBMux runs it: started
// before the VM, since QEMU dials out to it when the guest's USB core comes up (usb-tcp-addr), and serving the app's
// libimobiledevice calls on a loopback port of its own (USBMUXD_SOCKET_ADDRESS). Its pairing records live in the
// device's own data, so Erase forgets the pairing with the rest of the device. It runs in this process's job and ends
// with it however it ends.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace LightTouch.Native;

internal sealed class UsbMux : IDisposable
{
    /// The emulator's usb-tcp-addr.
    public string GuestAddress { get; }
    /// USBMUXD_SOCKET_ADDRESS for libimobiledevice.
    public string ClientAddress { get; }
    private readonly Process process;

    private UsbMux(Process process, string guest, string client)
    {
        this.process = process;
        GuestAddress = guest;
        ClientAddress = client;
    }

    public bool IsRunning
    {
        get
        {
            try { return !process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    /// Starts the daemon beside the executable; null (no app management) when it isn't there.
    public static UsbMux? Start(string configDirectory, string log)
    {
        var binary = Path.Combine(AppContext.BaseDirectory, "usbmuxd.exe");
        if (!File.Exists(binary)) return null;
        Directory.CreateDirectory(configDirectory);
        var guest = $"127.0.0.1:{FreePort()}";
        var client = $"127.0.0.1:{FreePort()}";
        // Inherited by the daemon: where QEMU dials it, and no delay for a VM restored already running.
        Environment.SetEnvironmentVariable("USBMUXD_QEMU_ADDR", guest);
        Environment.SetEnvironmentVariable("USBMUXD_QEMU_DELAY", "0");
        var process = ChildProcess.Start(binary, ["-f", "-v", "-S", client, "-P", "NONE", "-C", configDirectory], log,
            companion: true);
        Environment.SetEnvironmentVariable("USBMUXD_QEMU_ADDR", null);
        Environment.SetEnvironmentVariable("USBMUXD_QEMU_DELAY", null);
        // libusbmuxd reads it with getenv(), from msvcrt's copy of the environment.
        Qemu.SetEnvironment("USBMUXD_SOCKET_ADDRESS", client);
        return new UsbMux(process, guest, client);
    }

    /// A loopback port nothing listens on now. The daemon binds it moments later; a collision fails its start loudly
    /// (exclusive bind) rather than sharing.
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        process.Dispose();
    }
}

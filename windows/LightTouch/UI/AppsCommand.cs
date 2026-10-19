// `LightTouch.exe apps DEVICE_DIR list | install IPA... | remove BUNDLE_ID`: a running device's apps from a console or
// a script, over the device's own usbmuxd (the address its process records while it runs). Exit codes: 0 done,
// 1 failed, 2 usage, 3 the device isn't running.

using System.Runtime.InteropServices;
using LightTouch.Library;
using LightTouch.Native;
using LightTouch.Services;

namespace LightTouch.UI;

internal static partial class AppsCommand
{
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    public static int Run(string directory, string[] args)
    {
        // A GUI executable has no console of its own; print to the one it was started from.
        AttachConsole(-1);
        var record = DeviceLibrary.Read(directory);
        if (record == null)
        {
            Console.Error.WriteLine($"There's no device at {directory}.");
            return 2;
        }
        if (!File.Exists(record.UsbMuxAddress))
        {
            Console.Error.WriteLine($"{record.Name} isn't running.");
            return 3;
        }
        Qemu.SetEnvironment("USBMUXD_SOCKET_ADDRESS", File.ReadAllText(record.UsbMuxAddress).Trim());
        var services = new DeviceServices();
        try
        {
            switch (args)
            {
                case ["list"]:
                    foreach (var app in services.InstalledApps().GetAwaiter().GetResult())
                    {
                        Console.WriteLine($"{app.Id}\t{app.Version}\t{app.Name}");
                    }
                    return 0;
                case ["install", .. var ipas] when ipas.Length > 0:
                    foreach (var ipa in ipas)
                    {
                        services.Install(ipa, (percent, step) => Console.WriteLine($"{Path.GetFileName(ipa)}: {step} {percent}%"))
                            .GetAwaiter().GetResult();
                        Console.WriteLine($"{Path.GetFileName(ipa)}: installed");
                    }
                    return 0;
                case ["remove", var id]:
                    services.Uninstall(id).GetAwaiter().GetResult();
                    Console.WriteLine($"{id}: removed");
                    return 0;
                default:
                    Console.Error.WriteLine("usage: LightTouch.exe apps DEVICE_DIR list | install IPA... | remove BUNDLE_ID");
                    return 2;
            }
        }
        catch (Exception error) when (error is DeviceServiceException or IOException or InvalidDataException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}

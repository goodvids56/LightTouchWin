// Light Touch for Windows. With no arguments: the device library. `run DEVICE_DIR`: that one device, in this process
// (the library starts each device that way, as the Mac app spawns a LightTouchDevice helper per device).

using LightTouch.Library;
using LightTouch.UI;

namespace LightTouch;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Every failure reaches the device's emulator.log as well as the person.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Console.Error.WriteLine($"LightTouch: {e.Exception}");
            MessageBox.Show(e.Exception.Message, "Light Touch", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        // The emulator's DLLs live beside the executable.
        Environment.CurrentDirectory = AppContext.BaseDirectory;
        var library = new DeviceLibrary();
        if (args is ["run", var directory])
        {
            var record = DeviceLibrary.Read(directory);
            if (record == null)
            {
                MessageBox.Show($"There's no device at {directory}.", "Light Touch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 64;
            }
            using var form = new DeviceForm(record, library);
            Application.Run(form);
            Console.Error.WriteLine($"LightTouch: emulator exited with {form.ExitCode}");
            return form.ExitCode;
        }
        Application.Run(new LibraryForm(library));
        return 0;
    }
}

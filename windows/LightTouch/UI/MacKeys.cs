// Windows virtual keys as macOS virtual keycodes (kVK_*): qemu_ios_ui_key_mac maps those through QEMU's
// qemu_input_map_osx_to_qcode, so the emulator's keyboard path is the Mac app's.

namespace LightTouch.UI;

internal static class MacKeys
{
    private static readonly Dictionary<Keys, int> Map = new()
    {
        [Keys.A] = 0x00, [Keys.S] = 0x01, [Keys.D] = 0x02, [Keys.F] = 0x03, [Keys.H] = 0x04, [Keys.G] = 0x05,
        [Keys.Z] = 0x06, [Keys.X] = 0x07, [Keys.C] = 0x08, [Keys.V] = 0x09, [Keys.B] = 0x0B, [Keys.Q] = 0x0C,
        [Keys.W] = 0x0D, [Keys.E] = 0x0E, [Keys.R] = 0x0F, [Keys.Y] = 0x10, [Keys.T] = 0x11, [Keys.D1] = 0x12,
        [Keys.D2] = 0x13, [Keys.D3] = 0x14, [Keys.D4] = 0x15, [Keys.D6] = 0x16, [Keys.D5] = 0x17,
        [Keys.Oemplus] = 0x18, [Keys.D9] = 0x19, [Keys.D7] = 0x1A, [Keys.OemMinus] = 0x1B, [Keys.D8] = 0x1C,
        [Keys.D0] = 0x1D, [Keys.OemCloseBrackets] = 0x1E, [Keys.O] = 0x1F, [Keys.U] = 0x20,
        [Keys.OemOpenBrackets] = 0x21, [Keys.I] = 0x22, [Keys.P] = 0x23, [Keys.Return] = 0x24, [Keys.L] = 0x25,
        [Keys.J] = 0x26, [Keys.OemQuotes] = 0x27, [Keys.K] = 0x28, [Keys.OemSemicolon] = 0x29,
        [Keys.OemPipe] = 0x2A, [Keys.Oemcomma] = 0x2B, [Keys.OemQuestion] = 0x2C, [Keys.N] = 0x2D, [Keys.M] = 0x2E,
        [Keys.OemPeriod] = 0x2F, [Keys.Tab] = 0x30, [Keys.Space] = 0x31, [Keys.Oemtilde] = 0x32, [Keys.Back] = 0x33,
        [Keys.Escape] = 0x35, [Keys.LShiftKey] = 0x38, [Keys.ShiftKey] = 0x38, [Keys.Capital] = 0x39,
        [Keys.LMenu] = 0x3A, [Keys.Menu] = 0x3A, [Keys.RShiftKey] = 0x3C, [Keys.RMenu] = 0x3D,
        [Keys.Decimal] = 0x41, [Keys.Multiply] = 0x43, [Keys.Add] = 0x45, [Keys.Divide] = 0x4B,
        [Keys.Subtract] = 0x4E, [Keys.NumPad0] = 0x52, [Keys.NumPad1] = 0x53, [Keys.NumPad2] = 0x54,
        [Keys.NumPad3] = 0x55, [Keys.NumPad4] = 0x56, [Keys.NumPad5] = 0x57, [Keys.NumPad6] = 0x58,
        [Keys.NumPad7] = 0x59, [Keys.NumPad8] = 0x5B, [Keys.NumPad9] = 0x5C, [Keys.F5] = 0x60, [Keys.F6] = 0x61,
        [Keys.F7] = 0x62, [Keys.F3] = 0x63, [Keys.F8] = 0x64, [Keys.F9] = 0x65, [Keys.F11] = 0x67,
        [Keys.F10] = 0x6D, [Keys.F12] = 0x6F, [Keys.Home] = 0x73, [Keys.PageUp] = 0x74, [Keys.Delete] = 0x75,
        [Keys.F4] = 0x76, [Keys.End] = 0x77, [Keys.F2] = 0x78, [Keys.PageDown] = 0x79, [Keys.F1] = 0x7A,
        [Keys.Left] = 0x7B, [Keys.Right] = 0x7C, [Keys.Down] = 0x7D, [Keys.Up] = 0x7E,
    };

    /// The kVK_* code for a key, or -1 for one the guest has no use for.
    public static int Code(Keys key) => Map.TryGetValue(key & Keys.KeyCode, out var code) ? code : -1;
}

// One running device: its own process (LightTouch.exe run DEVICE_DIR), as LightTouchDevice is on the Mac, since
// qemu_ios_main can run once per process. Frames are polled from qemu_ios_ui_frame at the panel's rate and drawn as
// the emulator publishes them (it turns its own picture and un-rotates touches, ipod_touch_lcd_map_touch); mouse
// buttons are the finger, keys go to the guest as macOS keycodes, and closing the window pauses the VM (which flushes
// the storage) before it quits, as the helper's halt does.

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LightTouch.Device;
using LightTouch.Library;
using LightTouch.Native;

namespace LightTouch.UI;

internal sealed class DeviceForm : Form
{
    private readonly DeviceRecord record;
    private readonly DeviceLibrary library;
    private readonly AppSettings settings;
    private readonly ScreenView screen = new();
    private readonly ToolStrip toolbar = new() { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
    private readonly StatusStrip statusBar = new();
    private readonly ToolStripStatusLabel status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripButton internet = new("Internet") { CheckOnClick = false };
    private readonly System.Windows.Forms.Timer pump = new();
    private FileStream? lease;
    private UsbMux? usbMux;
    private Thread? qemuThread;
    private PreparedDevice? prepared;
    private ulong serial;
    private bool fingerDown, pinching, started, halting, exited;
    private volatile int exitCode;
    private ulong guestPasteboardSerial;
    private bool restricted;
    private int lastWidth, lastHeight;

    public DeviceForm(DeviceRecord record, DeviceLibrary library)
    {
        this.record = record;
        this.library = library;
        settings = library.Settings();
        Text = record.Name;
        KeyPreview = true;
        BackColor = Color.Black;
        Icon = LibraryForm.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(360, 560);

        AddButton("Home", "Home (Ctrl+H)", () => Press(Qemu.ButtonHome));
        AddButton("Lock", "Sleep/Wake (Ctrl+L)", () => Press(Qemu.ButtonPower));
        toolbar.Items.Add(new ToolStripSeparator());
        AddButton("Vol −", "Volume down", () => Press(Qemu.ButtonVolumeDown));
        AddButton("Vol +", "Volume up", () => Press(Qemu.ButtonVolumeUp));
        toolbar.Items.Add(new ToolStripSeparator());
        AddButton("↺", "Rotate left (Ctrl+Left)", () => Qemu.Rotate(clockwise: false));
        AddButton("↻", "Rotate right (Ctrl+Right)", () => Qemu.Rotate(clockwise: true));
        AddButton("Shake", "Shake (Ctrl+Shift+Z)", Qemu.Shake);
        toolbar.Items.Add(new ToolStripSeparator());
        AddButton("Paste", "Paste the clipboard's text into the device (Ctrl+V)", PasteClipboard);
        AddButton("Screenshot", "Save a screenshot (Ctrl+S)", SaveScreenshot);
        internet.ToolTipText = "This boot runs Setup offline. Open the network once Setup is done.";
        internet.Click += (_, _) => OpenNetwork();
        internet.Visible = false;
        toolbar.Items.Add(internet);
        var power = new ToolStripDropDownButton("Power") { Alignment = ToolStripItemAlignment.Right };
        power.DropDownItems.Add("Shut Down", null, (_, _) => GuestShutdown());
        power.DropDownItems.Add("Restart", null, (_, _) => { if (Qemu.Ready()) Qemu.Reset(); });
        power.DropDownItems.Add("Hold Sleep/Wake (slide to power off)", null, (_, _) => HoldPower());
        toolbar.Items.Add(power);

        // The form is black for the letterbox; the bars keep the system's colors.
        toolbar.BackColor = statusBar.BackColor = SystemColors.Control;
        statusBar.Items.Add(status);
        screen.Dock = DockStyle.Fill;
        Controls.Add(screen);
        Controls.Add(toolbar);
        Controls.Add(statusBar);

        screen.MouseDown += OnScreenMouseDown;
        screen.MouseMove += OnScreenMouseMove;
        screen.MouseUp += OnScreenMouseUp;
        pump.Interval = 15;
        pump.Tick += (_, _) => Tick();
    }

    private void AddButton(string text, string tip, Action action)
    {
        var button = new ToolStripButton(text) { ToolTipText = tip, DisplayStyle = ToolStripItemDisplayStyle.Text };
        button.Click += (_, _) => { if (started) action(); screen.Focus(); };
        toolbar.Items.Add(button);
    }

    // MARK: - Boot

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            lease = new FileStream(record.Lease, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            Fail($"{record.Name} is already running.");
            return;
        }
        var board = record.BoardFacts;
        if (board == null)
        {
            Fail($"Light Touch doesn't know the board {record.Board}.");
            return;
        }
        Directory.CreateDirectory(record.Logs);
        SetStatus("Preparing…");
        try
        {
            prepared = await Task.Run(() => PreparedDevice.Prepare(board, record.Base, record.Overlay,
                record.WritableNor, library.BootRom(board),
                (done, total) => BeginInvoke(() => SetStatus($"Preparing storage (first start)… {done * 100 / Math.Max(total, 1)}%"))));
        }
        catch (Exception error)
        {
            Fail(error.Message);
            return;
        }
        Boot(board, prepared);
    }

    private void Boot(Board board, PreparedDevice device)
    {
        Qemu.DeviceInfo hardware;
        try
        {
            var major = Qemu.ApiVersion() >> 16;
            if (major != Qemu.ApiMajor)
            {
                Fail($"qemu-arm.dll speaks API {Qemu.ApiVersionText()}; this app needs {Qemu.ApiMajor}.x.");
                return;
            }
            var machines = Qemu.Machines();
            hardware = machines.FirstOrDefault(m => m.BoardName == board.Id);
            if (hardware.Machine == IntPtr.Zero)
            {
                Fail($"The emulator has no machine for {board.Id}.");
                return;
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Fail($"Couldn't load the emulator (qemu-arm.dll): {error.Message}");
            return;
        }

        // QEMU exits at startup when DirectSound has no output device; a PC without one boots silent instead.
        var audio = settings.Audio && WaveOutGetNumDevs() > 0;
        // The USB bridge listens before the guest's USB core comes up and dials it.
        usbMux = UsbMux.Start(Path.Combine(record.Data, "usbmuxd"), Path.Combine(record.Logs, "usbmuxd.log"));
        var options = new BootOptions(record.Network, settings.LocalNetwork, audio, settings.VerboseBoot,
            UsbAddress: usbMux?.GuestAddress);
        var config = device.Configuration(hardware, options, Path.Combine(record.Logs, "serial.log"));
        restricted = record.Network && device.ExpectsSetup;
        internet.Visible = restricted;
        Qemu.UseBinaryFiles();
        foreach (var (key, value) in config.Environment) Qemu.SetEnvironment(key, value);
        Console.Error.WriteLine($"LightTouch: qemu-arm.dll API {Qemu.ApiVersionText()}, booting {config.Machine}: "
            + string.Join(' ', config.Argv));
        Console.Error.Flush();
        File.Delete(Path.Combine(record.Directory, "clean-stop"));

        Qemu.Attach(IntPtr.Zero, IntPtr.Zero);
        qemuThread = new Thread(() =>
        {
            var rc = Qemu.Run(config.Argv);
            exitCode = rc;
            exited = true;
            try { BeginInvoke(OnQemuExited); } catch (InvalidOperationException) { }
        }, 16 << 20)
        { Name = "qemu-main", IsBackground = true };
        qemuThread.Start();
        started = true;
        SetStatus($"Starting {board.MarketingName}…");
        pump.Start();
        screen.Focus();
    }

    [DllImport("winmm.dll", EntryPoint = "waveOutGetNumDevs")]
    private static extern uint WaveOutGetNumDevs();

    private void OnQemuExited()
    {
        pump.Stop();
        if (exitCode == 0 && halting && !Qemu.StorageFailed())
        {
            File.WriteAllText(Path.Combine(record.Directory, "clean-stop"), "");
        }
        lease?.Dispose();
        lease = null;
        if (!halting && exitCode != 0)
        {
            MessageBox.Show(this, $"The emulator stopped (exit code {exitCode}). Its log is in {record.Logs}.",
                record.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        Close();
    }

    private void Fail(string message)
    {
        SetStatus(message);
        MessageBox.Show(this, message, record.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        lease?.Dispose();
        lease = null;
        exited = true;
        Close();
    }

    private void SetStatus(string text)
    {
        status.Text = text;
        screen.Placeholder = text;
    }

    // MARK: - Frames

    private void Tick()
    {
        if (!Qemu.Ready()) return;
        if (Qemu.StorageFailed()) SetStatus("The device's storage failed to write. Stop it and check the disk.");
        if (Qemu.GuestShutdownConfirmed() && !halting)
        {
            SetStatus("Powered off.");
            Halt();
            return;
        }
        if (Qemu.GuestPasteboard(ref guestPasteboardSerial) is { Length: > 0 } copied)
        {
            try { Clipboard.SetText(copied); } catch (ExternalException) { }
        }
        if (!Qemu.Frame(out var pixels, out var width, out var height, ref serial) || pixels == IntPtr.Zero
            || width <= 0 || height <= 0)
        {
            return;
        }
        screen.ShowFrame(pixels, width, height);
        if (width != lastWidth || height != lastHeight)
        {
            if (lastWidth == 0) SetStatus("Running");
            lastWidth = width;
            lastHeight = height;
            FitWindow(width, height);
        }
    }

    /// Sizes the window to the frame at the chosen scale, keeping it on screen.
    private void FitWindow(int width, int height)
    {
        if (WindowState != FormWindowState.Normal) return;
        var scale = settings.Scale / 100.0 * DeviceDpi / 96.0;
        var area = Screen.FromControl(this).WorkingArea;
        var chrome = toolbar.Height + statusBar.Height;
        var fit = Math.Min(1, Math.Min((area.Width - 40.0) / (width * scale), (area.Height - 80.0 - chrome) / (height * scale)));
        var size = new Size((int)(width * scale * fit), (int)(height * scale * fit) + chrome);
        size.Width = Math.Max(size.Width, toolbar.PreferredSize.Width);
        var center = new Point(Left + Width / 2, Top + Height / 2);
        ClientSize = size;
        Location = new Point(Math.Clamp(center.X - Width / 2, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(center.Y - Height / 2, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    // MARK: - Input

    /// LTW_TRACE_INPUT=1: every touch sent, in emulator.log.
    private static readonly bool TraceInput = Environment.GetEnvironmentVariable("LTW_TRACE_INPUT") == "1";

    private static void SendTouch(int phase, PointF p)
    {
        if (TraceInput) Console.Error.WriteLine($"LightTouch: touch {phase} {p.X:F3} {p.Y:F3}");
        Qemu.Touch(0, phase, p.X, p.Y);
    }

    private void OnScreenMouseDown(object? sender, MouseEventArgs e)
    {
        if (!started || e.Button != MouseButtons.Left || screen.Normalize(e.Location) is not { } p) return;
        screen.Capture = true;
        fingerDown = true;
        // Alt-drag: a second finger mirrored through the screen's center, for pinches.
        pinching = (ModifierKeys & Keys.Alt) != 0;
        SendTouch(Qemu.TouchBegin, p);
        if (pinching) Qemu.Touch2(Qemu.TouchBegin, 1 - p.X, 1 - p.Y);
    }

    private void OnScreenMouseMove(object? sender, MouseEventArgs e)
    {
        if (!fingerDown) return;
        var p = screen.Normalize(e.Location, clamp: true)!.Value;
        SendTouch(Qemu.TouchUpdate, p);
        if (pinching) Qemu.Touch2(Qemu.TouchUpdate, 1 - p.X, 1 - p.Y);
    }

    private void OnScreenMouseUp(object? sender, MouseEventArgs e)
    {
        if (!fingerDown || e.Button != MouseButtons.Left) return;
        var p = screen.Normalize(e.Location, clamp: true)!.Value;
        SendTouch(Qemu.TouchEnd, p);
        if (pinching) Qemu.Touch2(Qemu.TouchEnd, 1 - p.X, 1 - p.Y);
        fingerDown = pinching = false;
        screen.Capture = false;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!started) return base.ProcessCmdKey(ref msg, keyData);
        switch (keyData)
        {
            case Keys.Control | Keys.H: Press(Qemu.ButtonHome); return true;
            case Keys.Control | Keys.L: Press(Qemu.ButtonPower); return true;
            case Keys.Control | Keys.Left: Qemu.Rotate(clockwise: false); return true;
            case Keys.Control | Keys.Right: Qemu.Rotate(clockwise: true); return true;
            case Keys.Control | Keys.Up: Press(Qemu.ButtonVolumeUp); return true;
            case Keys.Control | Keys.Down: Press(Qemu.ButtonVolumeDown); return true;
            case Keys.Control | Keys.Shift | Keys.Z: Qemu.Shake(); return true;
            case Keys.Control | Keys.V: PasteClipboard(); return true;
            case Keys.Control | Keys.S: SaveScreenshot(); return true;
        }
        // Arrows, Tab and Return would otherwise move focus between the toolbar's buttons.
        if ((keyData & (Keys.Control | Keys.Alt)) == 0 && MacKeys.Code(keyData) >= 0)
        {
            return false;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!started || e.Control) return;
        var code = MacKeys.Code(e.KeyCode);
        if (code < 0) return;
        Qemu.KeyMac(code, down: true);
        e.Handled = e.SuppressKeyPress = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (!started) return;
        var code = MacKeys.Code(e.KeyCode);
        if (code < 0) return;
        Qemu.KeyMac(code, down: false);
        e.Handled = true;
    }

    private static void Press(int button)
    {
        Qemu.Button(button, down: true);
        var release = new System.Windows.Forms.Timer { Interval = 120 };
        release.Tick += (_, _) =>
        {
            Qemu.Button(button, down: false);
            release.Dispose();
        };
        release.Start();
    }

    /// Holds Sleep/Wake for the slide-to-power-off screen.
    private static void HoldPower()
    {
        Qemu.Button(Qemu.ButtonPower, down: true);
        var release = new System.Windows.Forms.Timer { Interval = 3500 };
        release.Tick += (_, _) =>
        {
            Qemu.Button(Qemu.ButtonPower, down: false);
            release.Dispose();
        };
        release.Start();
    }

    private void PasteClipboard()
    {
        if (!Clipboard.ContainsText()) return;
        Qemu.Paste(Clipboard.GetText());
        SetStatus("Pasted the clipboard's text into the device's pasteboard.");
    }

    private void SaveScreenshot()
    {
        using var image = screen.Snapshot();
        if (image == null) return;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Light Touch");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{record.Name} {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
        image.Save(path, ImageFormat.Png);
        SetStatus($"Saved {path}");
    }

    /// Lifts the Setup restriction: the guest keeps its Wi-Fi association and DHCP lease.
    private void OpenNetwork()
    {
        if (!restricted || !Qemu.Ready()) return;
        Qemu.NetRestrict("wifi0", false);
        if (prepared != null) File.WriteAllText(prepared.SetupDoneMark, "");
        restricted = false;
        internet.Visible = false;
        SetStatus("Internet open.");
    }

    // MARK: - Stopping

    private void GuestShutdown()
    {
        if (!Qemu.Ready()) return;
        switch (Qemu.Shutdown())
        {
            case 0: Halt(); break;
            default: SetStatus("Shutting down…"); break;
        }
    }

    /// Pauses the VM (vm_stop flushes the storage) and quits QEMU; the window closes when qemu_ios_main returns.
    private void Halt()
    {
        if (halting) return;
        halting = true;
        SetStatus("Stopping…");
        var thread = new Thread(() =>
        {
            var start = DateTime.UtcNow;
            // Still in qemu_init: nothing can be scheduled on the VM yet, and nothing is written.
            while (!Qemu.Ready() && !exited && DateTime.UtcNow - start < TimeSpan.FromSeconds(5)) Thread.Sleep(50);
            if (Qemu.Ready()) Qemu.Pause();
            if (!exited) Qemu.Quit();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!exited && DateTime.UtcNow < deadline) Thread.Sleep(50);
            if (!exited)
            {
                Console.Error.WriteLine("LightTouch: QEMU did not return; exiting");
                Environment.Exit(1);
            }
        }) { IsBackground = true };
        thread.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (started && !exited)
        {
            e.Cancel = true;
            Halt();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        usbMux?.Dispose();
        lease?.Dispose();
    }

    public int ExitCode => exitCode;
}

/// The device's screen: the newest frame, scaled to fit with its aspect kept, letterboxed in black.
internal sealed class ScreenView : Control
{
    private Bitmap? frame;
    private string placeholder = "";

    public ScreenView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Black;
        ForeColor = Color.Gray;
        TabStop = true;
    }

    public string Placeholder
    {
        get => placeholder;
        set
        {
            placeholder = value;
            Invalidate();
        }
    }

    /// Copies a BGRA frame (tightly packed) into the bitmap; the emulator's pixels stay valid only briefly.
    public void ShowFrame(IntPtr pixels, int width, int height)
    {
        if (frame == null || frame.Width != width || frame.Height != height)
        {
            frame?.Dispose();
            frame = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        }
        var data = frame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            unsafe
            {
                for (var y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy((byte*)pixels + (long)y * width * 4, (byte*)data.Scan0 + (long)y * data.Stride,
                        data.Stride, width * 4L);
                }
            }
        }
        finally
        {
            frame.UnlockBits(data);
        }
        Invalidate();
    }

    public Bitmap? Snapshot() => frame == null ? null : new Bitmap(frame);

    /// Every key is the guest's while the screen has focus: arrows, Tab and Return never move it.
    protected override bool IsInputKey(Keys keyData) => true;

    private RectangleF Fitted()
    {
        if (frame == null || Width == 0 || Height == 0) return RectangleF.Empty;
        var scale = Math.Min((float)Width / frame.Width, (float)Height / frame.Height);
        var w = frame.Width * scale;
        var h = frame.Height * scale;
        return new RectangleF((Width - w) / 2, (Height - h) / 2, w, h);
    }

    /// A point over the control as 0…1 over the frame, y down; null outside it (clamped instead with `clamp`).
    public PointF? Normalize(Point p, bool clamp = false)
    {
        var r = Fitted();
        if (r.Width <= 0) return clamp ? new PointF(0.5f, 0.5f) : null;
        var x = (p.X - r.X) / r.Width;
        var y = (p.Y - r.Y) / r.Height;
        if (clamp) return new PointF(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
        return x is >= 0 and <= 1 && y is >= 0 and <= 1 ? new PointF(x, y) : null;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);
        if (frame == null)
        {
            TextRenderer.DrawText(g, placeholder, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        var r = Fitted();
        var integral = Math.Abs(r.Width / frame.Width - Math.Round(r.Width / frame.Width)) < 0.01;
        g.InterpolationMode = integral ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(frame, r);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) frame?.Dispose();
        base.Dispose(disposing);
    }
}

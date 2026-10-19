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
using LightTouch.Proxy;
using LightTouch.Services;

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
    private readonly DeviceServices services = new();
    private readonly ToolStripDropDownButton appsMenu = new("Apps") { Enabled = false };
    private readonly System.Windows.Forms.Timer usbWatch = new() { Interval = 5000 };
    private readonly SemaphoreSlim installs = new(1, 1);
    private readonly GuestAgent agent = new();
    private WebProxy? webProxy;
    private WebProxySettings proxySettings = new();
    private bool proxyApplied, proxyApplying;
    private readonly ToolStripDropDownButton proxyMenu = new("Web") { Visible = false };
    private bool usbReady;
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
        appsMenu.ToolTipText = "Install and remove apps. Available once the device has connected over USB.";
        appsMenu.DropDownItems.Add("Install App…", null, (_, _) => PickApps());
        appsMenu.DropDownItems.Add("Installed Apps…", null, (_, _) => ShowApps());
        appsMenu.DropDownItems.Add(new ToolStripSeparator());
        appsMenu.DropDownItems.Add("Import Media…", null, (_, _) => PickMedia());
        toolbar.Items.Add(appsMenu);
        proxyMenu.ToolTipText = "How the device reaches the web: through Light Touch's proxy, which speaks the old TLS it "
            + "knows, to the live web or to the Wayback Machine at a date.";
        proxyMenu.DropDownItems.Add("Live Web", null, (_, _) => SetProxyMode("direct"));
        proxyMenu.DropDownItems.Add("Dated Web (Wayback Machine)…", null, (_, _) => SetProxyMode("archive"));
        proxyMenu.DropDownItems.Add("Off (no proxy)", null, (_, _) => SetProxyMode("off"));
        toolbar.Items.Add(proxyMenu);
        toolbar.Items.Add(power);
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = DroppedFiles(e).Length > 0 && usbReady ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += async (_, e) => await AddAll(DroppedFiles(e));
        usbWatch.Tick += async (_, _) => await CheckUsb();

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
        if (usbMux != null) File.WriteAllText(record.UsbMuxAddress, usbMux.ClientAddress);
        // The web proxy listens before the guest's first request (WebProxy.GuestForward on the netdev).
        if (record.Network) StartWebProxy();
        var options = new BootOptions(record.Network, settings.LocalNetwork, audio, settings.VerboseBoot,
            UsbAddress: usbMux?.GuestAddress, GuestForward: webProxy?.GuestForward ?? "");
        var config = device.Configuration(hardware, options, Path.Combine(record.Logs, "serial.log"));
        restricted = record.Network && device.ExpectsSetup;
        internet.Visible = restricted;
        if (webProxy != null) webProxy.Offline = restricted;
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
        if (usbMux != null) usbWatch.Start();
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

    // MARK: - Apps

    /// Polls until installation_proxy answers: the guest connects over USB about a minute into its start, and its
    /// services come up a little after lockdownd.
    private async Task CheckUsb()
    {
        if (usbReady || halting) return;
        if (usbMux?.IsRunning != true)
        {
            usbWatch.Stop();
            appsMenu.ToolTipText = "The USB bridge stopped; restart the device for app management.";
            return;
        }
        usbWatch.Stop();
        if (await services.Ready())
        {
            usbReady = true;
            appsMenu.Enabled = true;
            appsMenu.ToolTipText = "Install and remove apps, and import songs, photos and videos. You can also drop them on the window.";
            SetStatus("Connected over USB");
            _ = ApplyWebProxy();
            return;
        }
        if (!halting) usbWatch.Start();
    }

    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => f.EndsWith(".ipa", StringComparison.OrdinalIgnoreCase) || MediaPreparation.Kind(f) != null).ToArray()
            : [];

    /// Dropped files in order: apps installed, media imported.
    private async Task AddAll(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            if (file.EndsWith(".ipa", StringComparison.OrdinalIgnoreCase)) await InstallOne(file);
            else await ImportMedia(file);
        }
    }

    private void PickApps()
    {
        using var dialog = new OpenFileDialog { Title = "Install App", Filter = "iOS apps (*.ipa)|*.ipa", Multiselect = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) _ = InstallAll(dialog.FileNames);
    }

    private void ShowApps()
    {
        using var form = new AppsForm(record.Name, services, InstallOne);
        form.ShowDialog(this);
    }

    private async Task InstallAll(IEnumerable<string> ipas)
    {
        foreach (var ipa in ipas) await InstallOne(ipa);
    }

    /// One .ipa, after any install already running; the status bar follows it and a failure says why.
    private async Task InstallOne(string ipa)
    {
        var name = Path.GetFileNameWithoutExtension(ipa);
        try
        {
            name = DeviceServices.ReadIpa(ipa)?.Name ?? name;
        }
        catch (InvalidDataException)
        {
        }
        await installs.WaitAsync();
        try
        {
            SetStatus($"Installing {name}…");
            await services.Install(ipa, (percent, step) => BeginInvoke(() =>
                SetStatus(percent >= 0 ? $"Installing {name}: {step} {percent}%" : $"Installing {name}: {step}")));
            SetStatus($"Installed {name}.");
        }
        catch (Exception error) when (error is DeviceServiceException or IOException or InvalidDataException)
        {
            SetStatus($"{name} didn't install.");
            MessageBox.Show(this, $"{name} didn't install: {error.Message}", "Install App", MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            installs.Release();
        }
    }

    // MARK: - Web proxy

    private void StartWebProxy()
    {
        proxySettings = WebProxySettings.Load(record);
        try
        {
            proxySettings.Save(record);
            webProxy = new WebProxy(WebProxySettings.Config(record)) { LocalNetwork = settings.LocalNetwork };
            webProxy.Start();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or System.Net.Sockets.SocketException)
        {
            Console.Error.WriteLine($"LightTouch: the web proxy didn't start: {error.Message}");
            webProxy = null;
            return;
        }
        proxyMenu.Visible = true;
        UpdateProxyMenu();
    }

    private void UpdateProxyMenu()
    {
        var items = proxyMenu.DropDownItems.OfType<ToolStripMenuItem>().ToList();
        items[0].Checked = proxySettings.Mode == "direct";
        items[1].Checked = proxySettings.Mode == "archive";
        items[1].Text = proxySettings.Mode == "archive" && DateTime.TryParseExact(proxySettings.ArchiveDate, "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
            ? $"Dated Web (Wayback Machine, {date:d MMM yyyy})…"
            : "Dated Web (Wayback Machine)…";
        items[2].Checked = proxySettings.Mode == "off";
    }

    /// The routing takes effect with the next connection; turning the proxy on also sets the guest up for it.
    private void SetProxyMode(string mode)
    {
        if (webProxy == null) return;
        var archiveDate = proxySettings.ArchiveDate;
        if (mode == "archive")
        {
            var shown = DateTime.TryParseExact(archiveDate, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var current) ? current.ToString("yyyy-MM-dd") : "2009-09-09";
            var answer = Prompt.Show(this, "Dated Web", "Browse the web as the Wayback Machine has it on (YYYY-MM-DD):", shown);
            if (answer == null) return;
            if (!DateTime.TryParseExact(answer.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var picked) || picked.Year < 1996 || picked > DateTime.Today)
            {
                MessageBox.Show(this, "Enter a date from 1996 to today, as YYYY-MM-DD.", "Dated Web", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            archiveDate = picked.ToString("yyyyMMdd");
        }
        proxySettings = new WebProxySettings { Mode = mode, ArchiveDate = archiveDate };
        try
        {
            proxySettings.Save(record);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, error.Message, "Web", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        UpdateProxyMenu();
        SetStatus(mode switch
        {
            "direct" => "The device reaches the live web through the proxy.",
            "archive" => "The device browses the Wayback Machine's web.",
            _ => "The proxy passes the device's connections straight through.",
        });
        _ = ApplyWebProxy();
    }

    /// Once a boot, when the proxy is on and the guest's agent answers: the route to the proxy and the CA's trust
    /// (WebProxySetup), so HTTPS through it works without a prompt.
    private async Task ApplyWebProxy()
    {
        if (webProxy == null || proxyApplied || proxyApplying || restricted || !usbReady || proxySettings.Mode == "off") return;
        proxyApplying = true;
        try
        {
            for (var i = 0; i < 60 && !GuestAgent.IsAlive; i++) await Task.Delay(1000);
            if (!GuestAgent.IsAlive || halting) return;
            await new WebProxySetup(agent, WebProxySettings.Config(record), library.GuestTools).Configure();
            proxyApplied = true;
            Console.Error.WriteLine("LightTouch: proxy: certificate trusted through the guest agent");
        }
        catch (Exception error) when (error is DeviceServiceException or GuestAgentException or IOException
            or System.Security.Cryptography.CryptographicException or InvalidDataException)
        {
            Console.Error.WriteLine($"LightTouch: proxy: setup failed: {error.Message}");
            SetStatus("The web proxy couldn't be set up on the device; secure sites may not load.");
        }
        finally
        {
            proxyApplying = false;
        }
    }

    // MARK: - Media

    private void PickMedia()
    {
        using var dialog = new OpenFileDialog { Title = "Import Media", Filter = MediaPreparation.DialogFilter, Multiselect = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) _ = AddAll(dialog.FileNames);
    }

    /// One song, photo or video, after anything already being added: prepared on this PC, staged over AFC, then
    /// committed into its library by the guest's helper. The status bar follows it and a failure says why.
    private async Task ImportMedia(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        if (MediaPreparation.Kind(file) is not { } kind) return;
        if (MediaImport.Refusal(record, kind) is { } refusal)
        {
            MessageBox.Show(this, refusal, "Import Media", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        await installs.WaitAsync();
        PreparedMedia? media = null;
        try
        {
            SetStatus($"Preparing {name}…");
            media = await MediaPreparation.Prepare(file, record.BoardFacts);
            if (!GuestAgent.IsAlive)
            {
                throw new MediaException($"The {record.BoardFacts?.ShortName ?? "device"} isn't ready yet. Try again when it has finished starting.");
            }
            var import = new MediaImport(services, agent, library.GuestTools);
            var title = media.Title;
            await import.Stage(media, f => BeginInvoke(() => SetStatus($"Copying {title}: {(int)(f * 100)}%")));
            SetStatus($"Adding {title} to {media.Destination}…");
            await import.Commit(media);
            SetStatus($"Added {title} to {media.Destination}.");
        }
        catch (Exception error) when (error is MediaException or DeviceServiceException or GuestAgentException or IOException
            or UnauthorizedAccessException)
        {
            SetStatus($"{name} wasn't added.");
            MessageBox.Show(this, $"{name} wasn't added: {error.Message}", "Import Media", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            media?.Delete();
            installs.Release();
        }
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
        if (webProxy != null) webProxy.Offline = false;
        SetStatus("Internet open.");
        _ = ApplyWebProxy();
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
        agent.Dispose();
        webProxy?.Dispose();
        File.Delete(record.UsbMuxAddress);
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

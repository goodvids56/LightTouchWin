// The device library: every device the app keeps, how to add one, and its start, erase and delete. Each started
// device is its own process (DeviceForm), whose emulator output goes to the device's logs\emulator.log.

using System.Diagnostics;
using LightTouch.Library;
using LightTouch.Native;

namespace LightTouch.UI;

internal sealed class LibraryForm : Form
{
    public static readonly Icon AppIcon = SystemIcons.Application;

    private readonly DeviceLibrary library;
    private readonly ListView list = new()
    {
        View = View.Details,
        Dock = DockStyle.Fill,
        MultiSelect = false,
        HideSelection = false,
        FullRowSelect = true,
        BorderStyle = BorderStyle.None,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly Label empty = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = SystemColors.GrayText,
        Text = "No devices yet.\n\nAdd ▸ Import from Light Touch for Mac… unpacks the built-in iPod touch (iOS 3.1.3) and its "
            + "SecureROM from the Mac app's download.\nAdd ▸ Import Prepared Device… takes a device made by firmwarekit create.",
    };
    private readonly Dictionary<string, Process> running = [];
    private readonly ToolStripButton start = new("Start");
    private readonly ToolStripDropDownButton manage = new("Device");
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };

    public LibraryForm(DeviceLibrary library)
    {
        this.library = library;
        Text = "Light Touch";
        Icon = AppIcon;
        ClientSize = new Size(560, 420);
        MinimumSize = new Size(420, 300);
        StartPosition = FormStartPosition.CenterScreen;

        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
        start.Click += (_, _) => StartSelected();
        toolbar.Items.Add(start);
        var add = new ToolStripDropDownButton("Add");
        add.DropDownItems.Add("Import from Light Touch for Mac…", null, (_, _) => ImportMacApp());
        add.DropDownItems.Add("Import Prepared Device…", null, (_, _) => ImportBase());
        toolbar.Items.Add(add);
        manage.DropDownItems.Add("Rename…", null, (_, _) => RenameSelected());
        var network = new ToolStripMenuItem("Internet Access") { CheckOnClick = true };
        network.Click += (_, _) => WithSelected(r =>
        {
            r.Network = network.Checked;
            DeviceLibrary.Save(r);
        });
        manage.DropDownOpening += (_, _) => network.Checked = Selected?.Network ?? false;
        manage.DropDownItems.Add(network);
        manage.DropDownItems.Add("Show Files", null, (_, _) => WithSelected(r => Open(r.Directory)));
        manage.DropDownItems.Add("Show Logs", null, (_, _) => WithSelected(r =>
        {
            Directory.CreateDirectory(r.Logs);
            Open(r.Logs);
        }));
        manage.DropDownItems.Add(new ToolStripSeparator());
        manage.DropDownItems.Add("Erase All Content and Settings…", null, (_, _) => EraseSelected());
        manage.DropDownItems.Add("Delete…", null, (_, _) => DeleteSelected());
        toolbar.Items.Add(manage);
        toolbar.Items.Add(SettingsMenu());
        var help = new ToolStripDropDownButton("Help") { Alignment = ToolStripItemAlignment.Right };
        help.DropDownItems.Add("Keyboard and Mouse", null, (_, _) => ShowControls());
        help.DropDownItems.Add("About Light Touch for Windows", null, (_, _) => ShowAbout());
        toolbar.Items.Add(help);

        list.Columns.Add("Name", 200);
        list.Columns.Add("Device", 320);
        list.Resize += (_, _) => list.Columns[1].Width = Math.Max(120, list.ClientSize.Width - list.Columns[0].Width);
        list.DoubleClick += (_, _) => StartSelected();
        list.SelectedIndexChanged += (_, _) => UpdateActions();
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) StartSelected();
            if (e.KeyCode == Keys.Delete) DeleteSelected();
        };
        list.ContextMenuStrip = new ContextMenuStrip();
        list.ContextMenuStrip.Opening += (_, e) =>
        {
            list.ContextMenuStrip.Items.Clear();
            if (Selected == null)
            {
                e.Cancel = true;
                return;
            }
            list.ContextMenuStrip.Items.Add("Start", null, (_, _) => StartSelected());
            foreach (ToolStripItem item in manage.DropDownItems)
            {
                list.ContextMenuStrip.Items.Add(item is ToolStripSeparator
                    ? new ToolStripSeparator()
                    : new ToolStripMenuItem(item.Text, null, (_, _) => item.PerformClick()));
            }
        };

        Controls.Add(list);
        Controls.Add(empty);
        Controls.Add(toolbar);
        refresh.Tick += (_, _) => UpdateRunning();
        refresh.Start();
        Reload();
    }

    private ToolStripDropDownButton SettingsMenu()
    {
        var menu = new ToolStripDropDownButton("Settings");
        var settings = library.Settings();
        var audio = new ToolStripMenuItem("Sound") { CheckOnClick = true, Checked = settings.Audio };
        audio.Click += (_, _) => Change(s => s.Audio = audio.Checked);
        var verbose = new ToolStripMenuItem("Verbose Boot") { CheckOnClick = true, Checked = settings.VerboseBoot };
        verbose.Click += (_, _) => Change(s => s.VerboseBoot = verbose.Checked);
        var lan = new ToolStripMenuItem("Attach to Local Network") { CheckOnClick = true, Checked = settings.LocalNetwork };
        lan.ToolTipText = "Let devices reach this PC's local network (printers, file shares). Off: the internet only.";
        lan.Click += (_, _) => Change(s => s.LocalNetwork = lan.Checked);
        var scale = new ToolStripMenuItem("Window Size");
        foreach (var percent in new[] { 100, 150, 200 })
        {
            var item = new ToolStripMenuItem($"{percent}%") { Checked = settings.Scale == percent };
            item.Click += (_, _) =>
            {
                Change(s => s.Scale = percent);
                foreach (ToolStripMenuItem other in scale.DropDownItems) other.Checked = other == item;
            };
            scale.DropDownItems.Add(item);
        }
        menu.DropDownItems.AddRange([audio, verbose, lan, scale, new ToolStripSeparator()]);
        menu.DropDownItems.Add("Open Assets Folder", null, (_, _) => Open(library.Assets));
        menu.DropDownItems.Add(new ToolStripLabel("Settings apply at a device's next start.") { ForeColor = SystemColors.GrayText });
        return menu;
    }

    private void Change(Action<AppSettings> change)
    {
        var settings = library.Settings();
        change(settings);
        library.Save(settings);
    }

    // MARK: - The list

    private DeviceRecord? Selected => list.SelectedItems.Count == 1 ? list.SelectedItems[0].Tag as DeviceRecord : null;

    private void WithSelected(Action<DeviceRecord> action)
    {
        if (Selected is { } record) action(record);
        Reload();
    }

    private void Reload()
    {
        var selected = Selected?.Id;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var record in library.Load())
        {
            var item = new ListViewItem(record.Name) { Tag = record, Name = record.Id };
            item.SubItems.Add(record.Subtitle + (IsRunning(record) ? " · Running" : ""));
            list.Items.Add(item);
            if (record.Id == selected) item.Selected = true;
        }
        if (list.SelectedItems.Count == 0 && list.Items.Count > 0) list.Items[0].Selected = true;
        list.EndUpdate();
        empty.Visible = list.Items.Count == 0;
        list.Visible = !empty.Visible;
        UpdateActions();
    }

    private void UpdateActions()
    {
        var record = Selected;
        start.Enabled = record != null;
        start.Text = record != null && IsRunning(record) ? "Show" : "Start";
        manage.Enabled = record != null;
    }

    private bool IsRunning(DeviceRecord record) =>
        (running.TryGetValue(record.Id, out var p) && !p.HasExited) || DeviceLibrary.IsRunning(record);

    private void UpdateRunning()
    {
        var ended = running.Where(p => p.Value.HasExited).ToList();
        foreach (var (id, process) in ended)
        {
            running.Remove(id);
            // A device that stops on its own with a failure (the emulator refused its boot) says why.
            if (ExitCode(process) is { } code and not 0 and not 64
                && library.Load().FirstOrDefault(r => r.Id == id) is { } record)
            {
                ShowFailure(record, code);
            }
        }
        if (ended.Count > 0) Reload();
    }

    private static int? ExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return null; }
    }

    private void ShowFailure(DeviceRecord record, int code)
    {
        var log = Path.Combine(record.Logs, "emulator.log");
        var tail = "";
        try
        {
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            tail = string.Join("\n", reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0 && !l.StartsWith("LightTouch: qemu-arm.dll")).TakeLast(6));
        }
        catch (IOException)
        {
        }
        MessageBox.Show(this, $"{record.Name} stopped (exit code {code}).\n\n{tail}\n\nThe whole log is {log}.", Text,
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // MARK: - Actions

    private void StartSelected()
    {
        if (Selected is not { } record) return;
        if (running.TryGetValue(record.Id, out var process) && !process.HasExited)
        {
            Foreground(process);
            return;
        }
        if (DeviceLibrary.IsRunning(record))
        {
            MessageBox.Show(this, $"{record.Name} is already running.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Directory.CreateDirectory(record.Logs);
        try
        {
            running[record.Id] = ChildProcess.Start(Environment.ProcessPath!, ["run", record.Directory],
                Path.Combine(record.Logs, "emulator.log"));
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"Couldn't start {record.Name}: {error.Message}", Text, MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        Reload();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    private static void Foreground(Process process)
    {
        process.Refresh();
        if (process.MainWindowHandle != IntPtr.Zero) SetForegroundWindow(process.MainWindowHandle);
    }

    private void ImportMacApp()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose Light Touch for Mac's download (LightTouchMac-universal.zip)",
            Filter = "Light Touch for Mac (*.zip)|*.zip|All files|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        // Importing again (for a newer release's SecureROMs or guest tools) needn't add a second built-in iPod.
        var addBuiltIn = !library.HasBuiltIn || MessageBox.Show(this,
            "The library already has the built-in iPod touch. Add another one?\n\nNo updates only the SecureROMs and the guest tools.",
            "Import from Light Touch for Mac", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        RunImport("Importing from Light Touch for Mac", (report, cancel) =>
            library.ImportMacApp(dialog.FileName, (step, fraction) => report(step, fraction), cancel, addBuiltIn));
    }

    private void ImportBase()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a prepared device: the folder firmwarekit create wrote (it holds device.lock.json).",
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        RunImport("Importing the device", (report, _) => library.ImportBase(dialog.SelectedPath, f => report("Copying", f)));
    }

    private void RunImport(string title, Func<Action<string, double>, CancellationToken, DeviceRecord?> work)
    {
        using var progress = new ProgressForm(title);
        DeviceRecord? added = null;
        Exception? failure = null;
        progress.Shown += async (_, _) =>
        {
            try
            {
                added = await Task.Run(() => work(progress.Report, progress.Cancellation));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error)
            {
                failure = error;
            }
            progress.Close();
        };
        progress.ShowDialog(this);
        Reload();
        if (failure != null)
        {
            MessageBox.Show(this, failure.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        else if (added != null)
        {
            if (list.Items.Find(added.Id, false).FirstOrDefault() is { } item) item.Selected = true;
        }
        else if (!progress.Cancellation.IsCancellationRequested)
        {
            MessageBox.Show(this, "Imported the SecureROMs and the guest tools.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void RenameSelected()
    {
        if (Selected is not { } record) return;
        var name = Prompt.Show(this, "Rename", "Name:", record.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        record.Name = name.Trim();
        DeviceLibrary.Save(record);
        Reload();
    }

    private void EraseSelected()
    {
        if (Selected is not { } record || !Stopped(record)) return;
        if (MessageBox.Show(this, $"Erase {record.Name}? Its apps, data and settings are removed; it starts as new.",
                "Erase All Content and Settings", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }
        DeviceLibrary.Erase(record);
        Reload();
    }

    private void DeleteSelected()
    {
        if (Selected is not { } record || !Stopped(record)) return;
        if (MessageBox.Show(this, $"Delete {record.Name} and everything on it?", "Delete",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }
        DeviceLibrary.Delete(record);
        Reload();
    }

    private bool Stopped(DeviceRecord record)
    {
        if (!IsRunning(record)) return true;
        MessageBox.Show(this, $"Stop {record.Name} first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }

    private static void Open(string folder) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });

    private void ShowControls() => MessageBox.Show(this,
        "Click and drag: touch. Alt-drag: pinch (a second finger mirrored through the center).\n"
        + "Typing goes to the device.\n\n"
        + "Ctrl+H  Home\nCtrl+L  Sleep/Wake\nCtrl+Up / Ctrl+Down  Volume\nCtrl+Left / Ctrl+Right  Rotate\n"
        + "Ctrl+Shift+Z  Shake\nCtrl+V  Paste the clipboard's text into the device\nCtrl+S  Screenshot (Pictures\\Light Touch)",
        "Keyboard and Mouse", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void ShowAbout() => MessageBox.Show(this,
        $"Light Touch for Windows {Application.ProductVersion}\n\n"
        + "A Windows port of Light Touch (github.com/samhenrigold/LightTouchMac) by Sam Henri Gold, "
        + "maintained as the fork LightTouchWin. The emulator is qemu-ios, built on QEMU.\n\n"
        + "Free software under the GNU GPL, version 2 or later. The licenses of the bundled components are in the "
        + "licenses folder beside LightTouch.exe.\n\n"
        + "iPhone, iPod touch and iPad are trademarks of Apple Inc. Light Touch is not affiliated with Apple.",
        "About Light Touch for Windows", MessageBoxButtons.OK, MessageBoxIcon.Information);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        refresh.Dispose();
        base.OnFormClosed(e);
    }
}

/// An import's progress, cancellable.
internal sealed class ProgressForm : Form
{
    private readonly Label label = new() { Dock = DockStyle.Top, Height = 32, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ProgressBar bar = new() { Dock = DockStyle.Top, Maximum = 1000 };
    private readonly CancellationTokenSource cancel = new();

    public ProgressForm(string title)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, 110);
        Padding = new Padding(12);
        var button = new Button { Text = "Cancel", Dock = DockStyle.Bottom };
        button.Click += (_, _) => cancel.Cancel();
        Controls.Add(button);
        Controls.Add(bar);
        Controls.Add(label);
        label.Text = "Starting…";
    }

    public CancellationToken Cancellation => cancel.Token;

    public void Report(string step, double fraction) => BeginInvoke(() =>
    {
        label.Text = $"{step}… {(int)(fraction * 100)}%";
        bar.Value = Math.Clamp((int)(fraction * 1000), 0, 1000);
    });

    protected override void Dispose(bool disposing)
    {
        if (disposing) cancel.Dispose();
        base.Dispose(disposing);
    }
}

/// A one-line text prompt.
internal static class Prompt
{
    public static string? Show(IWin32Window owner, string title, string caption, string value)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(360, 110),
            Padding = new Padding(12),
        };
        var label = new Label { Text = caption, Dock = DockStyle.Top, Height = 22 };
        var box = new TextBox { Text = value, Dock = DockStyle.Top };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom };
        form.Controls.AddRange([ok, box, label]);
        form.AcceptButton = ok;
        return form.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }
}

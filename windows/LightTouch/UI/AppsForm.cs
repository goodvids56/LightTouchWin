// The device's installed apps (its third-party ones, as the Mac's Apps inspector lists them), with Remove and
// Install App….

using LightTouch.Services;

namespace LightTouch.UI;

internal sealed class AppsForm : Form
{
    private readonly DeviceServices services;
    private readonly Func<string, Task> install;
    private readonly ListView list = new()
    {
        View = View.Details,
        Dock = DockStyle.Fill,
        FullRowSelect = true,
        MultiSelect = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button remove = new() { Text = "Remove", AutoSize = true, Enabled = false };

    public AppsForm(string deviceName, DeviceServices services, Func<string, Task> install)
    {
        this.services = services;
        this.install = install;
        Text = $"Apps on {deviceName}";
        Icon = LibraryForm.AppIcon;
        ClientSize = new Size(460, 360);
        StartPosition = FormStartPosition.CenterParent;
        list.Columns.Add("App", 260);
        list.Columns.Add("Version", 120);
        list.SelectedIndexChanged += (_, _) => remove.Enabled = list.SelectedItems.Count == 1;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var add = new Button { Text = "Install App…", AutoSize = true };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        add.Click += async (_, _) => await InstallPicked();
        remove.Click += async (_, _) => await RemoveSelected();
        refresh.Click += async (_, _) => await Reload();
        buttons.Controls.AddRange([add, remove, refresh]);
        Controls.Add(list);
        Controls.Add(status);
        Controls.Add(buttons);
        Shown += async (_, _) => await Reload();
    }

    public async Task Reload()
    {
        status.Text = "Loading…";
        try
        {
            var apps = await services.InstalledApps();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var app in apps)
            {
                list.Items.Add(new ListViewItem([app.Name, app.Version]) { Tag = app });
            }
            list.EndUpdate();
            status.Text = apps.Count == 0 ? "No apps installed." : $"{apps.Count} app{(apps.Count == 1 ? "" : "s")}";
        }
        catch (DeviceServiceException error)
        {
            status.Text = error.Message;
        }
    }

    private async Task InstallPicked()
    {
        using var dialog = new OpenFileDialog { Title = "Install App", Filter = "iOS apps (*.ipa)|*.ipa", Multiselect = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        foreach (var file in dialog.FileNames) await install(file);
        await Reload();
    }

    private async Task RemoveSelected()
    {
        if (list.SelectedItems.Count != 1 || list.SelectedItems[0].Tag is not InstalledApp app) return;
        if (MessageBox.Show(this, $"Remove {app.Name} and its data from the device?", "Remove App",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }
        status.Text = $"Removing {app.Name}…";
        try
        {
            await services.Uninstall(app.Id);
        }
        catch (DeviceServiceException error)
        {
            MessageBox.Show(this, error.Message, "Remove App", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        await Reload();
    }
}

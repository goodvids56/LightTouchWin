// One device's stock lockdown services over its usbmuxd (the Mac's LightTouchServices engine): the installed apps
// (installation_proxy browse), installing an .ipa (an AFC upload into PublicStaging, then Install with a status
// callback watched by an idle and an absolute deadline), uninstalling, and the AFC uploads media import makes. One
// operation at a time, on a worker thread, as the Mac's serial gate keeps them.

using System.Runtime.InteropServices;
using LightTouch.Native;
using static LightTouch.Native.IMobileDevice;

namespace LightTouch.Services;

internal sealed record InstalledApp(string Id, string Name, string Version);

internal sealed class DeviceServiceException(string message) : Exception(message);

internal sealed class DeviceServices
{
    /// installd's idle and absolute limits for one install (HostServiceWire Timeouts.installIdle/installAbsolute).
    private static readonly TimeSpan InstallIdle = TimeSpan.FromSeconds(300), InstallAbsolute = TimeSpan.FromSeconds(600);
    private const string Label = "LightTouchWin";
    private const int NoDevice = -3, AlreadyInstalledCode = -1;
    private readonly SemaphoreSlim gate = new(1, 1);

    // MARK: - Sessions

    /// A connected device and the services opened on it, freed together.
    private sealed class Session : IDisposable
    {
        public IntPtr Device;
        private readonly List<Action> cleanup = [];

        public static Session Open()
        {
            var r = DeviceNew(out var device, null);
            if (r != 0 || device == IntPtr.Zero)
            {
                throw new DeviceServiceException(r == NoDevice
                    ? "The device isn't connected over USB yet. It connects about a minute into its start."
                    : $"The device's USB connection failed ({r}).");
            }
            return new Session { Device = device };
        }

        /// A lockdown service's descriptor, through a lockdown session closed before the service connects (as
        /// libimobiledevice's service factory does).
        public IntPtr Start(string service)
        {
            var r = LockdownNew(Device, out var lockdown, Label);
            if (r != 0)
            {
                if (lockdown != IntPtr.Zero) LockdownFree(lockdown);
                throw new DeviceServiceException(r == -17
                    ? "The device is locked with a passcode. Unlock it and try again."
                    : $"The device's lockdown didn't answer ({r}). Try again once it has finished starting.");
            }
            r = LockdownStartService(lockdown, service, out var descriptor);
            LockdownFree(lockdown);
            if (r != 0) throw new DeviceServiceException($"The device couldn't start {service} ({r}).");
            cleanup.Add(() => ServiceDescriptorFree(descriptor));
            return descriptor;
        }

        public IntPtr Instproxy()
        {
            var descriptor = Start("com.apple.mobile.installation_proxy");
            if (InstproxyNew(Device, descriptor, out var client) != 0) throw new DeviceServiceException("installation_proxy refused.");
            cleanup.Insert(0, () => InstproxyFree(client));
            return client;
        }

        public IntPtr Afc()
        {
            var descriptor = Start("com.apple.afc");
            if (AfcNew(Device, descriptor, out var client) != 0) throw new DeviceServiceException("AFC refused.");
            cleanup.Insert(0, () => AfcFree(client));
            return client;
        }

        public string? LockdownValue(string key)
        {
            var r = LockdownNew(Device, out var lockdown, Label);
            if (r != 0) return null;
            try
            {
                return LockdownGetValue(lockdown, null, key, out var value) == 0 ? Take(value) as string : null;
            }
            finally
            {
                LockdownFree(lockdown);
            }
        }

        /// Leaves the handles to the library's own thread (an install that timed out may still be using them).
        public void Abandon()
        {
            cleanup.Clear();
            Device = IntPtr.Zero;
        }

        public void Dispose()
        {
            foreach (var free in cleanup) free();
            cleanup.Clear();
            if (Device != IntPtr.Zero) DeviceFree(Device);
            Device = IntPtr.Zero;
        }
    }

    private static object? Take(IntPtr plist)
    {
        try { return FromPlist(plist); }
        finally { if (plist != IntPtr.Zero) PlistFree(plist); }
    }

    /// `work` on a worker thread, one operation at a time.
    private async Task<T> Run<T>(Func<Session, T> work, CancellationToken cancel = default)
    {
        await gate.WaitAsync(cancel);
        try
        {
            return await Task.Run(() =>
            {
                using var session = Session.Open();
                return work(session);
            }, cancel);
        }
        finally
        {
            gate.Release();
        }
    }

    // MARK: - Queries

    /// Whether installation_proxy answers now: lockdownd comes up well before the services on a fresh boot.
    public async Task<bool> Ready()
    {
        try
        {
            return await Run(s =>
            {
                s.Instproxy();
                return true;
            });
        }
        catch (DeviceServiceException)
        {
            return false;
        }
    }

    public Task<string?> ProductVersion() => Run(s => s.LockdownValue("ProductVersion"));

    /// Third-party apps (ApplicationType User), by name.
    public Task<List<InstalledApp>> InstalledApps() => Run(s =>
    {
        var client = s.Instproxy();
        var options = ToPlist(new Dictionary<string, object> { ["ApplicationType"] = "User" });
        try
        {
            if (InstproxyBrowse(client, options, out var result) != 0) throw new DeviceServiceException("Couldn't list the apps.");
            var apps = (Take(result) as List<object> ?? []).OfType<Dictionary<string, object>>()
                .Select(d => d.TryGetValue("CFBundleIdentifier", out var id) && id is string bundle
                    ? new InstalledApp(bundle,
                        (d.GetValueOrDefault("CFBundleDisplayName") ?? d.GetValueOrDefault("CFBundleName")) as string ?? bundle,
                        (d.GetValueOrDefault("CFBundleVersion") ?? d.GetValueOrDefault("CFBundleShortVersionString")) as string ?? "")
                    : null)
                .OfType<InstalledApp>();
            return apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        finally
        {
            PlistFree(options);
        }
    });

    public Task<long?> FreeBytes() => Run(s =>
    {
        var afc = s.Afc();
        return AfcDeviceInfoKey(afc, "FSFreeBytes", out var value) == 0 && long.TryParse(TakeString(value), out var free)
            ? free
            : (long?)null;
    });

    // MARK: - Uninstall

    public Task Uninstall(string bundleID) => Run<bool>(s =>
    {
        var client = s.Instproxy();
        var r = InstproxyUninstall(client, bundleID, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (r != 0) throw new DeviceServiceException($"Couldn't remove {bundleID} ({r}).");
        return true;
    });

    // MARK: - Uploads (AFC)

    /// Uploads `local` to `remote` (relative to the media root), creating its folders; an interrupted upload leaves
    /// nothing at `remote`. `progress` gets the fraction sent.
    public Task<string> Upload(string local, string remote, Action<double>? progress = null, CancellationToken cancel = default) =>
        Run(s => UploadFile(s.Afc(), local, remote, progress, cancel), cancel);

    private static unsafe string UploadFile(IntPtr afc, string local, string remote, Action<double>? progress,
        CancellationToken cancel)
    {
        using var input = File.OpenRead(local);
        var total = input.Length;
        var parent = "";
        foreach (var component in remote.Split('/').SkipLast(1))
        {
            parent = parent.Length == 0 ? component : parent + "/" + component;
            AfcMakeDirectory(afc, parent);
        }
        var partial = $"{remote}.upload-{Guid.NewGuid():N}";
        if (AfcOpen(afc, partial, AfcWriteOnly, out var handle) != 0) throw new DeviceServiceException($"Couldn't create {remote}.");
        var complete = false;
        try
        {
            var buffer = new byte[1 << 16];
            long written = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                fixed (byte* p = buffer)
                {
                    for (var offset = 0; offset < read;)
                    {
                        if (AfcWrite(afc, handle, p + offset, (uint)(read - offset), out var count) != 0 || count == 0)
                        {
                            throw new DeviceServiceException($"The upload stopped after {written} of {total} bytes.");
                        }
                        offset += (int)count;
                        written += count;
                    }
                }
                progress?.Invoke(total == 0 ? 1 : (double)written / total);
            }
            if (AfcClose(afc, handle) != 0) throw new DeviceServiceException("The upload didn't finish.");
            handle = 0;
            AfcRemove(afc, remote);
            if (AfcRename(afc, partial, remote) != 0) throw new DeviceServiceException($"Couldn't place {remote}.");
            complete = true;
            progress?.Invoke(1);
            return remote;
        }
        finally
        {
            if (handle != 0) AfcClose(afc, handle);
            if (!complete) AfcRemove(afc, partial);
        }
    }

    // MARK: - Install

    /// The .ipa's app: Payload/<name>.app/Info.plist's identifier and name.
    public static (string Id, string Name)? ReadIpa(string ipa)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(ipa);
        var entry = zip.Entries.FirstOrDefault(e =>
            System.Text.RegularExpressions.Regex.IsMatch(e.FullName, @"^Payload/[^/]+\.app/Info\.plist$"));
        if (entry == null) return null;
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        if (ParsePlist(buffer.ToArray()) is not Dictionary<string, object> info
            || info.GetValueOrDefault("CFBundleIdentifier") is not string id)
        {
            return null;
        }
        var name = (info.GetValueOrDefault("CFBundleDisplayName") ?? info.GetValueOrDefault("CFBundleName")) as string ?? id;
        return (id, name);
    }

    private enum Command { Install, ArchiveData, Restore }

    /// Installs `ipa`, as a new version of an installed app too. iPhone OS 2.x's installd refuses an installed app
    /// (ApplicationAlreadyInstalled), has no upgrade and has consumed the upload by then: it is sent again and
    /// installed replacing the old app, keeping its data (a documents-only archive, the install, the restore).
    public async Task Install(string ipa, Action<int, string>? progress = null, CancellationToken cancel = default)
    {
        var bundle = ReadIpa(ipa) ?? throw new DeviceServiceException("That file isn't an iOS app (.ipa).");
        string? restoring = null;
        if ((await ProductVersion())?.StartsWith("2.", StringComparison.Ordinal) == true
            && (await ArchivedApps()).Contains(bundle.Id))
        {
            restoring = bundle.Id;
        }
        progress?.Invoke(0, "Uploading");
        var staged = await Upload(ipa, $"PublicStaging/LightTouch-{Guid.NewGuid():N}.ipa",
            f => progress?.Invoke((int)(f * 20), "Uploading"), cancel);
        try
        {
            await Perform(Command.Install, staged, progress, cancel);
            if (restoring != null) await Perform(Command.Restore, restoring, progress, cancel);
        }
        catch (DeviceServiceException e) when (e.Message.Contains("ApplicationAlreadyInstalled", StringComparison.Ordinal))
        {
            var again = await Upload(ipa, $"PublicStaging/LightTouch-{Guid.NewGuid():N}.ipa",
                f => progress?.Invoke((int)(f * 20), "Uploading"), cancel);
            await Perform(Command.ArchiveData, bundle.Id, progress, cancel);
            try
            {
                await Perform(Command.Install, again, progress, cancel);
            }
            catch (DeviceServiceException failed)
            {
                throw new DeviceServiceException($"The new version didn't install ({failed.Message}). The app's data is "
                    + "kept on the device and comes back the next time this app installs.");
            }
            await Perform(Command.Restore, bundle.Id, progress, cancel);
        }
    }

    private Task<List<string>> ArchivedApps() => Run(s =>
    {
        var client = s.Instproxy();
        // An empty ClientOptions, never none: 2.x drops a request without the key.
        var options = ToPlist(new Dictionary<string, object>());
        try
        {
            return InstproxyLookupArchives(client, options, out var result) == 0 && Take(result) is Dictionary<string, object> d
                ? d.Keys.ToList()
                : [];
        }
        finally
        {
            PlistFree(options);
        }
    });

    /// One installation_proxy command, its status watched until Complete, an error, or a deadline.
    private Task Perform(Command command, string target, Action<int, string>? progress, CancellationToken cancel) => Run<bool>(s =>
    {
        cancel.ThrowIfCancellationRequested();
        var client = s.Instproxy();
        var done = new ManualResetEventSlim();
        string? failure = null;
        var lastActivity = DateTime.UtcNow;
        StatusCallback callback = (_, status, _) =>
        {
            lastActivity = DateTime.UtcNow;
            if (StatusGetError(status, out var name, out var description, out _) != 0 || name != IntPtr.Zero)
            {
                var errorName = TakeString(name);
                var detail = TakeString(description);
                failure = string.Join(": ", new[] { errorName, detail }.Where(t => !string.IsNullOrEmpty(t)));
                if (failure.Length == 0) failure = "install failed";
                done.Set();
                return;
            }
            StatusGetName(status, out var statusName);
            var text = TakeString(statusName) ?? "";
            if (text == "Complete")
            {
                done.Set();
                return;
            }
            StatusGetPercent(status, out var percent);
            progress?.Invoke(percent < 0 ? -1 : 20 + percent * 80 / 100, text);
        };
        var handle = GCHandle.Alloc(callback);
        var pointer = Marshal.GetFunctionPointerForDelegate(callback);
        var options = ToPlist(command == Command.Install
            ? new Dictionary<string, object>()
            : new Dictionary<string, object> { ["ArchiveType"] = "DocumentsOnly" });
        var started = DateTime.UtcNow;
        try
        {
            var r = command switch
            {
                Command.Install => InstproxyInstall(client, target, options, pointer, IntPtr.Zero),
                Command.ArchiveData => InstproxyArchive(client, target, options, pointer, IntPtr.Zero),
                _ => InstproxyRestore(client, target, options, pointer, IntPtr.Zero),
            };
            if (r != 0) throw new DeviceServiceException($"installation_proxy didn't take the request ({r}).");
            while (!done.Wait(TimeSpan.FromSeconds(1)))
            {
                var now = DateTime.UtcNow;
                if (now - lastActivity > InstallIdle || now - started > InstallAbsolute)
                {
                    // The library's status thread may still be live: its handles and the callback stay allocated.
                    s.Abandon();
                    handle = default;
                    throw new DeviceServiceException("The install stopped answering.");
                }
            }
            // Freeing the client joins the status thread, so the callback is released after it.
            s.Dispose();
            if (failure != null) throw new DeviceServiceException(failure);
            return true;
        }
        finally
        {
            PlistFree(options);
            if (handle.IsAllocated) handle.Free();
        }
    }, cancel);
}

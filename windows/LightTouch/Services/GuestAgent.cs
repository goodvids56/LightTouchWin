// The guest agent over the running device's qemu_ios_agent_* calls (qemu-ios contrib/it-agent/README.md), in the
// device's own process as LightTouchDevice's AgentDispatcher runs it on the Mac: each request carries an id, a poll
// routes results to their requests and cancels what outlives its deadline. The ops are GuestAgent.swift's typed v2
// ones with their v1 `exec` fallbacks, as far as media import uses them.

using System.Collections.Concurrent;
using System.Text;
using LightTouch.Native;

namespace LightTouch.Services;

internal sealed class GuestAgentException(string operation, int status, byte[] output)
    : Exception($"agent {operation} failed ({status}): {Encoding.UTF8.GetString(output.AsSpan(0, Math.Min(4096, output.Length)))}")
{
    public const int NotFound = -2, TooBig = -27;
    public string Operation { get; } = operation;
    public int Status { get; } = status;
}

internal sealed class GuestAgent : IDisposable
{
    private sealed record Pending(DateTime Deadline, TaskCompletionSource<string?> Reply);

    private readonly ConcurrentDictionary<string, Pending> pending = new();
    private readonly System.Threading.Timer timer;
    private readonly object pollLock = new();
    private HashSet<string>? ops;
    private int? version;

    public GuestAgent()
    {
        timer = new System.Threading.Timer(_ => Poll(), null, 50, 50);
    }

    public static bool IsAlive => Qemu.Ready() && Qemu.AgentStatus() == 1;

    /// The raw request: the op's status and output; DeviceServiceException when the device can't answer.
    public async Task<(int Status, byte[] Output)> Raw(string operation, string arguments = "", byte[]? body = null,
        double deadline = 65, CancellationToken cancel = default)
    {
        if (!IsAlive) throw new DeviceServiceException("The device isn't ready yet. Try again when it has finished starting.");
        var id = Guid.NewGuid().ToString().ToUpperInvariant();
        var entry = new Pending(DateTime.UtcNow.AddSeconds(deadline),
            new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));
        pending[id] = entry;
        if (!Qemu.AgentRequest($"{id} {operation} {arguments}\n{Convert.ToBase64String(body ?? [])}"))
        {
            pending.TryRemove(id, out _);
            throw new DeviceServiceException("The device command queue is full or unavailable.");
        }
        string? wire;
        using (cancel.Register(() => { Qemu.AgentCancel(id); if (pending.TryRemove(id, out var p)) p.Reply.TrySetCanceled(); }))
        {
            wire = await entry.Reply.Task;
        }
        if (wire == null) throw new DeviceServiceException("The device command timed out; its outcome is unknown.");
        var newline = wire.IndexOf('\n');
        var header = newline < 0 ? [] : wire[..newline].Split(' ', 2);
        if (header.Length != 2 || header[0] != id || !int.TryParse(header[1], out var status))
        {
            throw new DeviceServiceException("The device returned an invalid command result.");
        }
        try
        {
            return (status, Convert.FromBase64String(wire[(newline + 1)..]));
        }
        catch (FormatException)
        {
            throw new DeviceServiceException("The device returned an invalid command result.");
        }
    }

    public async Task<byte[]> Perform(string operation, string arguments = "", byte[]? body = null,
        CancellationToken cancel = default)
    {
        var (status, output) = await Raw(operation, arguments, body, cancel: cancel);
        if (status != 0) throw new GuestAgentException(operation, status, output);
        return output;
    }

    /// Whether the agent's ping lists `op` (a v1 agent answers only its version).
    public async Task<bool> Has(string op)
    {
        if (ops == null)
        {
            var lines = Encoding.UTF8.GetString(await Perform("ping")).Split('\n');
            if (lines.Length == 0 || !lines[0].StartsWith("it_agent v", StringComparison.Ordinal)
                || !int.TryParse(lines[0]["it_agent v".Length..], out var v))
            {
                throw new DeviceServiceException("The device's guest tools didn't respond as expected. Restart the device to update them.");
            }
            version = v;
            ops = lines.FirstOrDefault(l => l.StartsWith("ops ", StringComparison.Ordinal))?[4..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet() ?? [];
        }
        return ops.Contains(op);
    }

    public int? Version => version;

    public static string Quote(string word) => "'" + word.Replace("'", "'\\''") + "'";

    private Task<byte[]> Shell(string command) => Perform("exec", command);

    public async Task<byte[]> Spawn(params string[] argv) => await Has("spawn")
        ? await Perform("spawn", body: Encoding.UTF8.GetBytes(string.Concat(argv.Select(a => a + "\0"))))
        : await Shell(string.Join(' ', argv.Select(Quote)));

    public async Task Put(string path, int mode, byte[] data)
    {
        var octal = Convert.ToString(mode, 8);
        const int part = 256 * 1024 - 4097;
        if (data.Length <= part)
        {
            await Perform("put", $"{path} {octal}", data);
            return;
        }
        if (!await Has("putpart"))
        {
            throw new DeviceServiceException("The device's guest tools are too old to receive this file. Restart the device to update them.");
        }
        for (var offset = 0; offset < data.Length;)
        {
            var end = Math.Min(offset + part, data.Length);
            await Perform("putpart", $"{offset} {(end == data.Length ? 1 : 0)} {octal} {path}", data[offset..end]);
            offset = end;
        }
    }

    public async Task<byte[]?> Get(string path)
    {
        var (status, output) = await Raw("get", path);
        if (status == GuestAgentException.NotFound) return null;
        if (status == GuestAgentException.TooBig)
        {
            var data = new List<byte>();
            while (true)
            {
                var piece = await Perform("getrange", $"{data.Count} {1024 * 1024} {path}");
                data.AddRange(piece);
                if (piece.Length < 1024 * 1024) return [.. data];
            }
        }
        if (status != 0) throw new GuestAgentException("get", status, output);
        return output;
    }

    public async Task Chown(int uid, int gid, string path)
    {
        if (await Has("chown")) await Perform("chown", $"{uid} {gid} {path}");
        else await Shell($"chown {uid}:{gid} {Quote(path)}");
    }

    public async Task Unlink(string path)
    {
        if (!await Has("unlink"))
        {
            await Shell($"rm -f {Quote(path)}");
            return;
        }
        var (status, output) = await Raw("unlink", path);
        if (status != 0 && status != GuestAgentException.NotFound) throw new GuestAgentException("unlink", status, output);
    }

    private void Poll()
    {
        if (!Monitor.TryEnter(pollLock)) return;
        try
        {
            if (pending.IsEmpty) return;
            string? wire;
            while ((wire = Qemu.AgentResult()) != null)
            {
                var id = wire[..Math.Max(0, wire.IndexOfAny([' ', '\n']))];
                if (pending.TryRemove(id, out var entry)) entry.Reply.TrySetResult(wire);
            }
            var running = Qemu.Ready();
            var now = DateTime.UtcNow;
            foreach (var (id, entry) in pending)
            {
                if (running && entry.Deadline > now) continue;
                if (!pending.TryRemove(id, out _)) continue;
                if (running)
                {
                    Qemu.AgentCancel(id);
                    entry.Reply.TrySetResult(null);
                }
                else
                {
                    entry.Reply.TrySetException(new DeviceServiceException("The device stopped before its command completed."));
                }
            }
        }
        finally
        {
            Monitor.Exit(pollLock);
        }
    }

    public void Dispose()
    {
        timer.Dispose();
        foreach (var (_, entry) in pending) entry.Reply.TrySetException(new DeviceServiceException("The device stopped."));
        pending.Clear();
    }
}

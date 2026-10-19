// Media import on the running device, as LightTouchCore's MediaImport does it on the Mac: the prepared file
// (MediaPreparation) staged over AFC into /LightTouch/<id>/ of the media partition, then committed into the device's
// library by the guest's itmedia (Music, Videos: with the metadata plist) or itphoto (Saved Photos) through the agent
// (GuestServices.commitMedia). The helpers come from Light Touch for Mac's guest.aar (DeviceLibrary.ImportMacApp).
// Which libraries a firmware takes media into is the catalog's `media` for its build (MediaSupport.swift).

using System.Reflection;
using System.Text;
using System.Text.Json;
using LightTouch.Library;

namespace LightTouch.Services;

internal sealed class MediaImport(DeviceServices services, GuestAgent agent, string guestTools)
{
    public async Task Stage(PreparedMedia media, Action<double>? progress = null, CancellationToken cancel = default)
    {
        if (!Guid.TryParse(media.Id, out _)) throw new MediaException("Invalid media staging identifier.");
        await services.Upload(media.File, $"LightTouch/{media.Id}/{Path.GetFileName(media.File)}", progress,
            reuseIdentical: true, cancel: cancel);
        if (media.Artwork != null)
        {
            await services.Upload(media.Artwork, $"LightTouch/{media.Id}/artwork.jpg", reuseIdentical: true, cancel: cancel);
        }
    }

    /// Adds the staged media to its library; MediaException in plain words when the guest refused or didn't confirm.
    public async Task Commit(PreparedMedia media)
    {
        var helper = media.Kind == MediaKind.Photo ? "itphoto" : "itmedia";
        bool confirmed;
        try
        {
            confirmed = await CommitStaged(media.Id, helper, media.Metadata);
        }
        catch (GuestAgentException error)
        {
            // The helper's reason goes to emulator.log; the person gets plain words.
            Console.Error.WriteLine($"LightTouch: media: {error.Message}");
            throw new MediaException($"{media.Destination} couldn't add “{media.Title}”.");
        }
        if (!confirmed)
        {
            throw new MediaException(media.Kind == MediaKind.Photo
                ? "Photos didn't confirm the import. Check Saved Photos before importing it again."
                : $"{media.Destination} didn't confirm the import. The copied media has been retained.");
        }
    }

    private async Task<bool> CommitStaged(string id, string helper, string? metadata)
    {
        var local = Path.Combine(guestTools, "tools", helper);
        if (!File.Exists(local))
        {
            throw new MediaException($"The guest's media helper ({helper}) is missing. In the library, Add ▸ Import from Light Touch for Mac… "
                + "again adds it.");
        }
        await agent.Chown(501, 501, "/var/mobile/Media/LightTouch");
        await agent.Chown(501, 501, $"/var/mobile/Media/LightTouch/{id}");
        var temporary = new List<string>();
        var arguments = new List<string> { id };
        try
        {
            if (metadata != null)
            {
                var remote = $"/tmp/ltm-media-{id}.plist";
                await agent.Put(remote, Convert.ToInt32("644", 8), await File.ReadAllBytesAsync(metadata));
                temporary.Add(remote);
                arguments.Insert(0, remote);
            }
            var executable = $"/tmp/ltm-{helper}-{id}";
            await agent.Put(executable, Convert.ToInt32("755", 8), await File.ReadAllBytesAsync(local));
            temporary.Add(executable);
            var output = await agent.Spawn([executable, .. arguments]);
            return Encoding.UTF8.GetString(output).EndsWith("imported\n", StringComparison.Ordinal);
        }
        finally
        {
            foreach (var path in temporary)
            {
                try { await agent.Unlink(path); } catch (Exception e) when (e is GuestAgentException or DeviceServiceException) { }
            }
        }
    }

    // MARK: - Support (MediaSupport.swift)

    private static readonly Lazy<Dictionary<string, (string[] Media, bool Prerelease)>> Catalog = new(LoadCatalog);

    /// The libraries `record`'s firmware takes media into ("Music", "Photos", "Videos"): the catalog entry's `media`
    /// for its board and build; none for a prerelease, whose helpers were never qualified.
    public static IReadOnlyCollection<string> Supported(DeviceRecord record) =>
        record.Build != null && Catalog.Value.TryGetValue($"{record.Board}-{record.Build}", out var entry) && !entry.Prerelease
            ? entry.Media
            : [];

    /// Null when `kind` can be added on this firmware; else why not.
    public static string? Refusal(DeviceRecord record, MediaKind kind)
    {
        var destination = PreparedMedia.DestinationOf(kind);
        if (Supported(record).Contains(destination)) return null;
        var what = kind switch { MediaKind.Song => "music", MediaKind.Photo => "photos", _ => "videos" };
        var name = record.Version != null ? $"iOS {record.Version}" + (record.Build != null ? $" ({record.Build})" : "") : "this firmware";
        return $"Adding {what} isn't supported on {name} yet.";
    }

    private static Dictionary<string, (string[] Media, bool Prerelease)> LoadCatalog()
    {
        var result = new Dictionary<string, (string[], bool)>(StringComparer.OrdinalIgnoreCase);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("firmware-catalog.json");
        if (stream == null) return result;
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return result;
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("board", out var board) || !entry.TryGetProperty("build", out var build)) continue;
            var media = entry.TryGetProperty("media", out var m) && m.ValueKind == JsonValueKind.Array
                ? m.EnumerateArray().Select(v => v.GetString()).OfType<string>().ToArray()
                : [];
            var prerelease = entry.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.String;
            result[$"{board.GetString()}-{build.GetString()}"] = (media, prerelease);
        }
        return result;
    }
}

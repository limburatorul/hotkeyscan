using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace HotkeyScan;

/// HotkeyScan is one portable .exe, so it updates itself in place: the newest GitHub release's
/// HotkeyScan.exe is downloaded beside the running one, checked against the size and SHA-256 GitHub
/// published for it, and swapped in. Windows lets a running .exe be renamed, so the old one steps
/// aside as HotkeyScan.exe.old (deleted at the next start) and nothing has to close: the next start
/// is the new version. A folder it may not write to (Program Files) is left alone.
internal static class Updater
{
    private const string Repo = "limburatorul/hotkeyscan", Asset = "HotkeyScan.exe";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static Version? waiting;

    public static Version Current => typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, v.Build) : new Version(0, 0, 0);

    /// <summary>The version now waiting for the next start, or null when there was nothing to do.</summary>
    public static async Task<Version?> UpdateInPlace()
    {
        // Already swapped in: the running copy is now the .old file, so a second download could never be
        // swapped in and would only cost another 66 MB every check. Nothing more until the next start.
        if (waiting is not null) return waiting;
        var exe = Environment.ProcessPath;
        // Only the published single-file exe replaces itself (under any name the user saved it as);
        // run from source or a build folder, Location is the HotkeyScan.dll beside it.
#pragma warning disable IL3000   // empty in a single-file app: exactly what is tested here
        if (exe is null || typeof(Updater).Assembly.Location != "") return null;
#pragma warning restore IL3000
        try { File.Delete(exe + ".old"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var fresh = exe + ".new";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            request.Headers.UserAgent.ParseAdd("HotkeyScan");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            if (!Version.TryParse((root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V'), out var latest) || latest <= Current) return null;

            var asset = root.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("name").GetString() == Asset);
            if (asset.ValueKind != JsonValueKind.Object) return null;
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            if (digest?.StartsWith("sha256:") != true) return null;   // nothing to check it against: not run

            await using (var source = await Http.GetStreamAsync(asset.GetProperty("browser_download_url").GetString()))
            await using (var file = File.Create(fresh))
                await source.CopyToAsync(file);

            // The size catches a cut-off download, the checksum everything else; a copy that differs is never run.
            if (new FileInfo(fresh).Length != asset.GetProperty("size").GetInt64()) { File.Delete(fresh); return null; }
            byte[] hash;
            await using (var file = File.OpenRead(fresh)) hash = await SHA256.HashDataAsync(file);
            if (!Convert.ToHexString(hash).Equals(digest[7..], StringComparison.OrdinalIgnoreCase)) { File.Delete(fresh); return null; }

            File.Move(exe, exe + ".old", overwrite: true);
            try { File.Move(fresh, exe); }
            catch { File.Move(exe + ".old", exe); throw; }   // never leave the folder without its .exe
            return waiting = latest;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException
                                       or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Offline, rate-limited, or a folder it may not write to: try again next time.
            try { File.Delete(fresh); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return null;
        }
    }
}

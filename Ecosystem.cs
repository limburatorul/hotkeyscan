using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HotkeyScan;

/// The Protagonist Labs apps find each other through %APPDATA%\ProtagonistLabs: each one writes a card
/// about itself into apps\. HotkeyScan writes its own, so Lume, Frostpane and Auto Clicker can send it a
/// combination they couldn't register (--check), and reads theirs for the Works with line. A card that is
/// missing, malformed or points at an exe that is gone just means that app isn't installed.
internal static class Ecosystem
{
    internal static readonly string Apps = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProtagonistLabs", "apps");

    public record Card(string Name, bool Paid, string Edition);

    public static void WriteCard()
    {
        var exe = Environment.ProcessPath;
        // Run from source (dotnet.exe): not something a sister could start.
        if (exe is null || !Path.GetFileName(exe).Equals("HotkeyScan.exe", StringComparison.OrdinalIgnoreCase)) return;
        var card = new JsonObject
        {
            ["schema"] = 1,
            ["updated"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["id"] = "hotkeyscan",
            ["name"] = "HotkeyScan",
            ["version"] = Updater.Current.ToString(),
            ["exe"] = exe,
            ["edition"] = "free",
            ["paid"] = false,
            ["page"] = "https://protagonistlabs.app/hotkeyscan/",
            ["commands"] = new JsonObject { ["check"] = new JsonArray("--check", "{combo}") },
        };
        var file = Path.Combine(Apps, "hotkeyscan.json");
        try
        {
            Directory.CreateDirectory(Apps);
            File.WriteAllText(file + ".tmp", card.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static Card? ReadCard(string id)
    {
        if (Load(Path.Combine(Apps, id + ".json")) is not { } card) return null;
        return new Card((string)card["name"]!, card["paid"] is JsonValue p && p.TryGetValue(out bool paid) && paid,
                        card["edition"] is JsonValue e && e.TryGetValue(out string? edition) ? edition : "");
    }

    /// A card is a few hundred bytes; anything past this is not one, and is not read into memory.
    private const long MaxCard = 64 * 1024;

    /// A sister's card when it is one we can trust to describe an installed app: schema 1, a name, and an exe
    /// that exists on a drive of this PC. Anything else, missing or hostile, is null - never an exception.
    internal static JsonObject? Load(string file)
    {
        try
        {
            if (new FileInfo(file).Length > MaxCard) return null;
            // ReadAllText drops a UTF-8 BOM, which an editor may have added and JsonNode would refuse.
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject card) return null;
            if (card["schema"] is not JsonValue schema || !schema.TryGetValue(out int version) || version != 1) return null;
            if (card["name"] is not JsonValue n || !n.TryGetValue(out string? name) || name.Length is 0 or > 64) return null;
            if (card["exe"] is not JsonValue x || !x.TryGetValue(out string? exe) || !IsLocalPath(exe) || !File.Exists(exe)) return null;
            return card;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or InvalidOperationException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// A full path on a drive of this PC. A UNC or device path (\\server\..., \\.\..., //server/...) is refused
    /// before File.Exists, which would otherwise reach out over the network for a stranger's card: Frostpane
    /// measured 21 s for a //host/share exe.
    private static bool IsLocalPath(string path) =>
        Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\") && !path.StartsWith("//")
        && !path.StartsWith(@"\/") && !path.StartsWith(@"/\");

    /// The sisters that hand HotkeyScan their taken hotkeys.
    public static readonly (string Id, string Name)[] Sisters = { ("lume", "Lume"), ("frostpane", "Frostpane"), ("autoclicker", "Auto Clicker") };

    public static readonly (string Id, string Name)[] Paid =
        { ("shelf", "Shelf"), ("stash", "Stash"), ("frostpane", "Frostpane"), ("reel", "Reel"), ("planner", "Planner"), ("reminder", "Reminder") };

    /// A sister's page, with a discount code only on the one offer line.
    public static string Page(string id, string? code) =>
        $"https://protagonistlabs.app/{id}/?{(code is null ? "" : $"code={code}&")}utm_source=hotkeyscan&utm_medium=app&utm_campaign=works-with";
}

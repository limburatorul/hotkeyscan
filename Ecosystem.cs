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
    private static readonly string Apps = Path.Combine(
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
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(Apps, id + ".json")));
            if (node?["schema"]?.GetValue<int>() != 1) return null;
            string? name = (string?)node["name"], exe = (string?)node["exe"];
            if (name is null || exe is null || !File.Exists(exe)) return null;
            return new Card(name, node["paid"]?.GetValue<bool>() == true, (string?)node["edition"] ?? "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or InvalidOperationException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// The sisters that hand HotkeyScan their taken hotkeys.
    public static readonly (string Id, string Name)[] Sisters = { ("lume", "Lume"), ("frostpane", "Frostpane"), ("autoclicker", "Auto Clicker") };

    public static readonly (string Id, string Name)[] Paid =
        { ("shelf", "Shelf"), ("stash", "Stash"), ("frostpane", "Frostpane"), ("reel", "Reel"), ("planner", "Planner"), ("reminder", "Reminder") };

    /// A sister's page, with a discount code only on the one offer line.
    public static string Page(string id, string? code) =>
        $"https://protagonistlabs.app/{id}/?{(code is null ? "" : $"code={code}&")}utm_source=hotkeyscan&utm_medium=app&utm_campaign=works-with";
}

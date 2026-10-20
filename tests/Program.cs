using System.Diagnostics;
using System.Text;
using HotkeyScan;

// The card reader on synthetic apps\ folders: Tests.exe [scratch folder]. Nothing here reads the real
// %APPDATA%\ProtagonistLabs, registers a hotkey or opens a window.
var root = Path.Combine(args.Length > 0 ? args[0] : Path.GetTempPath(), "hotkeyscan-cards-" + DateTime.Now.Ticks);
var exe = Path.Combine(root, "App.exe");
Directory.CreateDirectory(root);
File.WriteAllText(exe, "not really an exe; only its existence is checked");
int failures = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) failures++; }

string Folder(string name, params (string File, string Json)[] cards)
{
    var dir = Path.Combine(root, name);
    Directory.CreateDirectory(dir);
    foreach (var (file, json) in cards) File.WriteAllText(Path.Combine(dir, file), json);
    return dir;
}
string Card(string name, string combo, string does, bool registered = true, string? exePath = null, int schema = 1) =>
    $$"""{"schema":{{schema}},"id":"x","name":"{{name}}","exe":{{System.Text.Json.JsonSerializer.Serialize(exePath ?? exe)}},"hotkeys":[{"combo":"{{combo}}","does":"{{does}}","registered":{{(registered ? "true" : "false")}}}]}""";

var lume = ("lume.json", Card("Lume", "Space+Alt", "Show or hide Lume"));   // modifiers after the key on purpose
var frost = ("frostpane.json", Card("Frostpane", "Win+Alt+Space", "Peek at the desktop", registered: false));

// two apps on one combination
var two = AppScan.Studio(Folder("two", lume, ("frostpane.json", Card("Frostpane", "alt+space", "Peek at the desktop", registered: false))));
var clashes = AppScan.Clashes(two);
Check(two.Count == 2 && two.All(d => d.Combo.ToString() == "Alt+Space" && d.Source == AppScan.CardSource),
      "Space+Alt and alt+space are read as the same Alt+Space");
Check(clashes.Count == 1 && clashes.Values.Single() == "Frostpane and Lume both use Alt+Space — change it in one of them",
      "two apps on Alt+Space give one line: " + clashes.Values.SingleOrDefault());
Check(two.Single(d => d.App == "Lume").Registered == true && two.Single(d => d.App == "Frostpane").Registered == false,
      "registered is carried through as declared");

// three apps
var three = AppScan.Studio(Folder("three", lume, ("frostpane.json", Card("Frostpane", "Alt+Space", "Peek")),
                                  ("autoclicker.json", Card("Auto Clicker", "Alt+Space", "Starts and stops clicking"))));
Check(AppScan.Clashes(three).Values.SingleOrDefault() == "Auto Clicker, Frostpane and Lume all use Alt+Space — change it in all but one of them",
      "three apps give one line: " + AppScan.Clashes(three).Values.SingleOrDefault());

// the real three, no conflict
var none = AppScan.Studio(Folder("none", lume, frost, ("autoclicker.json", Card("Auto Clicker", "F6", "Starts and stops clicking"))));
Check(none.Count == 3 && AppScan.Clashes(none).Count == 0, "Alt+Space, Win+Alt+Space and F6: three hotkeys, no clash");
Check(none.Any(d => d.App == "Auto Clicker" && d.Combo == new Combo(KeyMods.None, 0x75)), "a bare F6 is read");

// one app declaring the same combination twice is not a clash with itself
var self = AppScan.Studio(Folder("self", ("lume.json",
    $$"""{"schema":1,"name":"Lume","exe":{{System.Text.Json.JsonSerializer.Serialize(exe)}},"hotkeys":[{"combo":"Alt+Space","does":"a"},{"combo":"Alt+Space","does":"b"}]}""")));
Check(self.Count == 2 && AppScan.Clashes(self).Count == 0 && self.All(d => d.Registered == null), "one app twice on a combo is no clash; no registered field is null");

// hostile cards: none of them may throw, and only the BOM one is read
var junk = new string('x', 8 * 1024 * 1024);
var many = string.Join(",", Enumerable.Range(1, 20).Select(i => $$"""{"combo":"Ctrl+Alt+{{(char)('A' + i)}}","does":"d{{i}}"}"""));
var hostile = Folder("hostile",
    ("schema2.json", Card("Schema Two", "Ctrl+Alt+1", "x", schema: 2)),
    ("unc.json", Card("Unc", "Ctrl+Alt+2", "x", exePath: @"\\host\share\App.exe")),
    ("slashes.json", Card("Slashes", "Ctrl+Alt+3", "x", exePath: "//host/share/App.exe")),
    // device-path spellings of the exe that does exist: only the local-path rule turns these away
    ("device.json", Card("Device", "Ctrl+Alt+4", "x", exePath: @"\\?\" + exe)),
    ("device2.json", Card("Device Two", "Ctrl+Alt+4", "x", exePath: @"\\.\" + exe)),
    ("device3.json", Card("Device Three", "Ctrl+Alt+4", "x", exePath: "//?/" + exe)),
    ("relative.json", Card("Relative", "Ctrl+Alt+5", "x", exePath: "App.exe")),
    ("missing.json", Card("Missing", "Ctrl+Alt+6", "x", exePath: Path.Combine(root, "gone.exe"))),
    ("junk.json", junk),
    ("twenty.json", $$"""{"schema":1,"name":"Twenty","exe":{{System.Text.Json.JsonSerializer.Serialize(exe)}},"hotkeys":[{{many}}]}"""),
    ("array.json", "[1,2,3]"),
    ("types.json", $$"""{"schema":"1","name":"Types","exe":{{System.Text.Json.JsonSerializer.Serialize(exe)}},"hotkeys":[]}"""),
    ("entries.json", $$"""{"schema":1,"name":"Entries","exe":{{System.Text.Json.JsonSerializer.Serialize(exe)}},"hotkeys":[{"combo":7,"does":"number combo"},{"combo":"Ctrl+Banana","does":"unknown key"},{"combo":"Ctrl+Alt+7","does":"{{new string('d', 201)}}"},{"combo":"Ctrl+Alt+8","does":""},"just a string",null,{"combo":"Ctrl+Alt+9","does":"kept","registered":"yes"}]}"""),
    ("deep.json", new string('[', 10000)),
    ("notjson.txt", Card("Txt", "Ctrl+Alt+0", "x")));
File.WriteAllText(Path.Combine(hostile, "bom.json"), Card("Bom", "Ctrl+Shift+B", "with a BOM"), new UTF8Encoding(true));
var watch = Stopwatch.StartNew();
List<Declared>? read = null;
try { read = AppScan.Studio(hostile); } catch (Exception e) { Check(false, "hostile folder threw " + e.GetType().Name); }
watch.Stop();
Check(read != null && read.Select(d => d.App).Distinct().OrderBy(n => n).SequenceEqual(new[] { "Bom", "Entries" }),
      "hostile folder: only the BOM card and the one good entry survive (" + string.Join(", ", read?.Select(d => $"{d.App}:{d.Combo}") ?? []) + ")");
Check(read?.SingleOrDefault(d => d.App == "Entries") is { What: "kept", Registered: null }, "registered: \"yes\" is not a bool, so it is unknown");
Check(watch.ElapsedMilliseconds < 2000, $"UNC and device exes refused without touching the network: {watch.ElapsedMilliseconds} ms for the whole folder");
Check(AppScan.Studio(Path.Combine(root, "no such folder")).Count == 0, "a missing apps folder is no hotkeys");

Directory.Delete(root, true);
Console.WriteLine(failures == 0 ? "all passed" : $"{failures} failed");
return failures == 0 ? 0 : 1;

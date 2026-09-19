using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HotkeyScan;

/// A hotkey an application declares in its own configuration.
internal record Declared(string App, string What, Combo Combo, string Source);

/// An application known to take hotkeys, and how it takes them.
internal record KnownApp(string Process, string Name, string Mechanism, string Defaults);

internal static class AppScan
{
    /// Applications that commonly hold hotkeys. "hook" means they never go through
    /// RegisterHotKey, so the probe cannot see them at all - only the empirical test can.
    private static readonly KnownApp[] Known =
    {
        new("discord", "Discord", "hook", "Ctrl+Shift+M mute, Ctrl+Shift+D deafen, plus your push-to-talk"),
        new("steam", "Steam", "hook", "Shift+Tab overlay, F12 screenshot"),
        new("nvidia share", "NVIDIA overlay (ShadowPlay)", "hook", "Alt+Z, Alt+F9 record, Alt+F10 replay, Alt+F1 screenshot"),
        new("nvidia app", "NVIDIA App", "hook", "Alt+Z and the ShadowPlay keys"),
        new("gamebar", "Xbox Game Bar", "system", "Win+G, Win+Alt+R, Win+Alt+PrtSc, Win+Alt+G"),
        new("gamebarftserver", "Xbox Game Bar", "system", "Win+G and friends"),
        new("autohotkey", "AutoHotkey", "hook", "whatever its script declares"),
        new("autohotkey64", "AutoHotkey v2", "hook", "whatever its script declares"),
        new("autohotkey32", "AutoHotkey v2", "hook", "whatever its script declares"),
        new("powertoys.settings", "PowerToys", "RegisterHotKey", "hotkeys read from its settings"),
        new("powertoys", "PowerToys", "RegisterHotKey", "hotkeys read from its settings"),
        new("sharex", "ShareX", "RegisterHotKey", "hotkeys read from its settings"),
        new("obs64", "OBS Studio", "RegisterHotKey", "hotkeys read from its settings"),
        new("spotify", "Spotify", "hook", "media keys"),
        new("lghub", "Logitech G HUB", "hook", "macro bindings you set per profile"),
        new("razer synapse 3", "Razer Synapse", "hook", "macro bindings you set per profile"),
        new("icue", "Corsair iCUE", "hook", "macro bindings you set per profile"),
        new("steelseriesgg", "SteelSeries GG", "hook", "macro bindings you set per profile"),
        new("snagit32", "Snagit", "RegisterHotKey", "PrintScreen"),
        new("greenshot", "Greenshot", "RegisterHotKey", "PrintScreen, Ctrl+PrintScreen, Alt+PrintScreen"),
        new("flameshot", "Flameshot", "RegisterHotKey", "PrintScreen"),
        new("ditto", "Ditto clipboard", "RegisterHotKey", "its paste hotkey, Ctrl+backtick by default"),
        new("everything", "Everything", "RegisterHotKey", "its search hotkey, whatever you set"),
        new("flow.launcher", "Flow Launcher", "hook", "Alt+Space"),
        new("wox", "Wox", "hook", "Alt+Space"),
        new("listary", "Listary", "hook", "double Ctrl"),
        new("teams", "Microsoft Teams", "hook", "Ctrl+Shift+M mute, Ctrl+Shift+O camera"),
        new("ms-teams", "Microsoft Teams", "hook", "Ctrl+Shift+M mute, Ctrl+Shift+O camera"),
        new("zoom", "Zoom", "RegisterHotKey", "Alt+A mute, Alt+V camera, if you made them global"),
        new("voicemeeter", "Voicemeeter", "hook", "whatever you bound to its macro buttons"),
        new("msiafterburner", "MSI Afterburner", "RegisterHotKey", "its OSD and capture keys"),
        new("streamdeck", "Elgato Stream Deck", "hook", "per-button bindings"),
        new("dropbox", "Dropbox", "RegisterHotKey", "screenshot capture, if enabled"),
        new("eartrumpet", "EarTrumpet", "RegisterHotKey", "its volume mixer hotkey"),
        new("translucenttb", "TranslucentTB", "RegisterHotKey", "none by default"),
        new("rainmeter", "Rainmeter", "RegisterHotKey", "per-skin bindings"),
        new("bandicam", "Bandicam", "RegisterHotKey", "F12 record, F11 capture"),
        new("copyq", "CopyQ", "RegisterHotKey", "its clipboard hotkeys"),
        new("idman", "Internet Download Manager", "hook", "Ctrl+Alt+D, Ctrl+F10"),
    };

    public static List<KnownApp> RunningKnownApps()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try { running.Add(p.ProcessName); } catch { }
        }
        return Known.Where(k => running.Contains(k.Process))
                    .GroupBy(k => k.Name).Select(g => g.First()).ToList();
    }

    // ---------- configuration files ----------

    public static List<Declared> DeclaredHotkeys()
    {
        var found = new List<Declared>();
        Safe(() => PowerToys(found));
        Safe(() => ShareX(found));
        Safe(() => Obs(found));
        Safe(() => AutoHotkey(found));
        return found;
    }

    /// One unreadable or reshaped config must not take the whole report down with it.
    private static void Safe(Action a)
    {
        try { a(); } catch (Exception ex) { Console.Error.WriteLine("  (config scan: " + ex.Message + ")"); }
    }

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string Docs => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// PowerToys stores every module's shortcut as {win,ctrl,alt,shift,code}, so one
    /// recursive shape match covers all modules instead of a rule per module.
    private static void PowerToys(List<Declared> found)
    {
        string root = Path.Combine(Local, "Microsoft", "PowerToys");
        if (!Directory.Exists(root)) return;

        foreach (var file in Directory.EnumerateFiles(root, "settings.json", SearchOption.AllDirectories))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            string module = Path.GetFileName(Path.GetDirectoryName(file)) ?? "PowerToys";
            Walk(doc.RootElement, module, (el, path) =>
            {
                if (!el.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number) return;
                if (!code.TryGetInt32(out int vk) || vk <= 0) return;

                var mods = KeyMods.None;
                if (Flag(el, "win")) mods |= KeyMods.Win;
                if (Flag(el, "ctrl")) mods |= KeyMods.Ctrl;
                if (Flag(el, "alt")) mods |= KeyMods.Alt;
                if (Flag(el, "shift")) mods |= KeyMods.Shift;
                if (mods == KeyMods.None) return;   // an unmodified "code" is a key list, not a shortcut

                found.Add(new Declared("PowerToys", path, new Combo(mods, (uint)vk), file));
            });
        }
    }

    private static bool Flag(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static void Walk(JsonElement el, string path, Action<JsonElement, string> onObject)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                onObject(el, path);
                foreach (var p in el.EnumerateObject()) Walk(p.Value, path + "." + p.Name, onObject);
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) Walk(item, path, onObject);
                break;
        }
    }

    private static void ShareX(List<Declared> found)
    {
        string[] candidates =
        {
            Path.Combine(Docs, "ShareX", "HotkeysConfig.json"),
            Path.Combine(Roaming, "ShareX", "HotkeysConfig.json"),
        };
        string? file = candidates.FirstOrDefault(File.Exists);
        if (file == null) return;

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (!doc.RootElement.TryGetProperty("Hotkeys", out var list)) return;

        foreach (var entry in list.EnumerateArray())
        {
            if (!entry.TryGetProperty("HotkeyInfo", out var info)) continue;
            if (!info.TryGetProperty("Hotkey", out var text) || text.ValueKind != JsonValueKind.String) continue;
            if (!TryParseDotNetKeys(text.GetString()!, out var combo)) continue;

            string what = "hotkey";
            if (entry.TryGetProperty("TaskSettings", out var ts))
            {
                if (ts.TryGetProperty("Description", out var d) && !string.IsNullOrWhiteSpace(d.GetString()))
                    what = d.GetString()!;
                else if (ts.TryGetProperty("Job", out var j))
                    what = j.ToString();
            }
            found.Add(new Declared("ShareX", what, combo, file));
        }
    }

    /// ShareX writes the System.Windows.Forms.Keys enum, e.g. "PrintScreen, Control, Shift".
    private static bool TryParseDotNetKeys(string text, out Combo combo)
    {
        combo = default;
        var mods = KeyMods.None;
        string? key = null;

        foreach (var token in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "control": mods |= KeyMods.Ctrl; continue;
                case "alt": mods |= KeyMods.Alt; continue;
                case "shift": mods |= KeyMods.Shift; continue;
                case "none": continue;
            }
            key = token;
        }

        if (key == null) return false;
        if (key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1])) key = key[1].ToString();
        key = key switch
        {
            "Oemtilde" => "`",
            "OemMinus" => "-",
            "Oemplus" => "=",
            "Oemcomma" => ",",
            "OemPeriod" => ".",
            "OemQuestion" => "/",
            "OemSemicolon" or "Oem1" => ";",
            "OemOpenBrackets" or "Oem4" => "[",
            "OemPipe" or "Oem5" => "\\",
            "OemCloseBrackets" or "Oem6" => "]",
            "OemQuotes" or "Oem7" => "'",
            "Back" => "Backspace",
            "Return" => "Enter",
            "Escape" => "Esc",
            _ => key,
        };
        if (key.StartsWith("NumPad")) key = "Num" + key[6..];
        return Keys.TryParse(key, out combo);
    }

    /// OBS keeps global hotkeys in the profile ini and per-scene ones in the scene json,
    /// but both use the same {control,alt,shift,key:"OBS_KEY_x"} binding shape.
    private static void Obs(List<Declared> found)
    {
        string root = Path.Combine(Roaming, "obs-studio", "basic");
        if (!Directory.Exists(root)) return;

        string profiles = Path.Combine(root, "profiles");
        if (Directory.Exists(profiles))
        {
            foreach (var ini in Directory.EnumerateFiles(profiles, "basic.ini", SearchOption.AllDirectories))
            {
                bool inHotkeys = false;
                foreach (var line in File.ReadLines(ini))
                {
                    string t = line.Trim();
                    if (t.StartsWith("["))
                    {
                        inHotkeys = t.Equals("[Hotkeys]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inHotkeys) continue;

                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    string value = t[(eq + 1)..].TrimStart();
                    if (!value.StartsWith("{")) continue;

                    using var doc = JsonDocument.Parse(value);
                    foreach (var c in ObsBindings(doc.RootElement))
                        found.Add(new Declared("OBS Studio", t[..eq], c, ini));
                }
            }
        }

        string scenes = Path.Combine(root, "scenes");
        if (!Directory.Exists(scenes)) return;
        foreach (var file in Directory.EnumerateFiles(scenes, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            Walk(doc.RootElement, "", (el, _) =>
            {
                if (!el.TryGetProperty("hotkeys", out var hk) || hk.ValueKind != JsonValueKind.Object) return;
                string name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "source" : "source";
                foreach (var p in hk.EnumerateObject())
                    foreach (var c in ObsBindings(p.Value))
                        found.Add(new Declared("OBS Studio", name + " / " + p.Name, c, file));
            });
        }
    }

    private static IEnumerable<Combo> ObsBindings(JsonElement el)
    {
        List<JsonElement> bindings;
        if (el.ValueKind == JsonValueKind.Array)
            bindings = el.EnumerateArray().ToList();
        else if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("bindings", out var b) && b.ValueKind == JsonValueKind.Array)
            bindings = b.EnumerateArray().ToList();
        else
            yield break;

        foreach (var binding in bindings)
        {
            if (binding.ValueKind != JsonValueKind.Object) continue;
            if (!binding.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.String) continue;
            string name = k.GetString()!;
            if (!name.StartsWith("OBS_KEY_")) continue;

            var mods = KeyMods.None;
            if (Flag(binding, "control")) mods |= KeyMods.Ctrl;
            if (Flag(binding, "alt")) mods |= KeyMods.Alt;
            if (Flag(binding, "shift")) mods |= KeyMods.Shift;
            if (Flag(binding, "command")) mods |= KeyMods.Win;

            if (Keys.TryParse(name[8..], out var parsed))
                yield return new Combo(mods, parsed.Vk);
        }
    }

    /// A running AutoHotkey script names its own file in the title of its hidden main window,
    /// which saves reading command lines out of other processes.
    private static void AutoHotkey(List<Declared> found)
    {
        var scripts = new List<string>();
        foreach (var h in Win32.AllWindows())
        {
            if (!Win32.ClassName(h).Equals("AutoHotkey", StringComparison.OrdinalIgnoreCase)) continue;
            string title = Win32.Title(h);
            int cut = title.IndexOf(" - AutoHotkey", StringComparison.OrdinalIgnoreCase);
            if (cut <= 0) continue;
            string path = title[..cut];
            if (File.Exists(path) && !scripts.Contains(path)) scripts.Add(path);
        }

        var pattern = new Regex(@"^[ \t]*([^\s;:]{1,40})::");
        foreach (var script in scripts)
        {
            foreach (var text in File.ReadLines(script))
            {
                var m = pattern.Match(text);
                if (!m.Success) continue;
                string token = m.Groups[1].Value;

                var mods = KeyMods.None;
                int i = 0;
                for (; i < token.Length; i++)
                {
                    if (token[i] == '#') { mods |= KeyMods.Win; continue; }
                    if (token[i] == '!') { mods |= KeyMods.Alt; continue; }
                    if (token[i] == '^') { mods |= KeyMods.Ctrl; continue; }
                    if (token[i] == '+') { mods |= KeyMods.Shift; continue; }
                    if (token[i] is '<' or '>' or '*' or '~' or '$') continue;
                    break;
                }
                if (i >= token.Length) continue;
                if (Keys.TryParse(token[i..], out var parsed))
                    found.Add(new Declared("AutoHotkey", token, new Combo(mods, parsed.Vk), script));
            }
        }
    }
}

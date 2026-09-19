using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace HotkeyScan;

/// Everything that can rewrite or drop a key before an application ever sees it, and that
/// can tell the left key from the right one - which the hotkey probe cannot.
internal static class Remaps
{
    public record Check(string Title, string Detail, bool Suspicious);

    public static List<Check> All()
    {
        var checks = new List<Check>();
        Safe(checks, "Registry scancode map", ScancodeMap);
        Safe(checks, "PowerToys Keyboard Manager", KeyboardManager);
        Safe(checks, "Keyboard filter drivers", FilterDrivers);
        Safe(checks, "Accessibility", Accessibility);
        return checks;
    }

    private static void Safe(List<Check> into, string title, Func<Check> read)
    {
        try { into.Add(read()); }
        catch (Exception ex) { into.Add(new Check(title, "could not be read: " + ex.Message, false)); }
    }

    // ---------- the driver-level remap ----------

    private static readonly Dictionary<ushort, string> Scancodes = new()
    {
        [0x001D] = "Left Ctrl", [0xE01D] = "Right Ctrl",
        [0x002A] = "Left Shift", [0x0036] = "Right Shift",
        [0x0038] = "Left Alt", [0xE038] = "Right Alt",
        [0x003A] = "Caps Lock", [0x0001] = "Esc", [0x000F] = "Tab",
        [0xE05B] = "Left Win", [0xE05C] = "Right Win", [0xE05D] = "Menu",
        [0x0000] = "nothing (disabled)",
    };

    private static string Scancode(ushort code) =>
        Scancodes.TryGetValue(code, out var name) ? name : $"scancode 0x{code:X4}";

    /// A "Scancode Map" rewrites keys inside the keyboard driver, below every hook, and it
    /// addresses the left and right key separately. If one exists, it explains everything.
    private static Check ScancodeMap()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Keyboard Layout");
        if (key?.GetValue("Scancode Map") is not byte[] map || map.Length < 16)
            return new Check("Registry scancode map", "none - no key is remapped in the driver", false);

        int count = BitConverter.ToInt32(map, 8);
        var lines = new List<string>();
        for (int i = 0; i < count - 1 && 12 + i * 4 + 3 < map.Length; i++)
        {
            ushort produced = BitConverter.ToUInt16(map, 12 + i * 4);
            ushort physical = BitConverter.ToUInt16(map, 12 + i * 4 + 2);
            lines.Add($"{Scancode(physical)}  ->  {Scancode(produced)}");
        }

        return new Check("Registry scancode map",
            lines.Count == 0 ? "present but empty" : string.Join("\n", lines), lines.Count > 0);
    }

    // ---------- PowerToys ----------

    /// Keyboard Manager stores remaps as semicolon-separated virtual-key codes, and it does
    /// distinguish Left Ctrl (162) from Right Ctrl (163).
    private static Check KeyboardManager()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "PowerToys", "Keyboard Manager");
        if (!Directory.Exists(dir))
            return new Check("PowerToys Keyboard Manager", "not configured on this PC", false);

        var lines = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var group in new[] { "remapKeys", "remapShortcuts" })
            {
                if (!doc.RootElement.TryGetProperty(group, out var section)) continue;
                foreach (var scope in section.EnumerateObject())
                {
                    if (scope.Value.ValueKind != JsonValueKind.Array) continue;
                    foreach (var entry in scope.Value.EnumerateArray())
                    {
                        string from = Combination(entry, "originalKeys");
                        string to = Combination(entry, "newRemapKeys");
                        if (from.Length == 0) continue;

                        string where = entry.TryGetProperty("targetApp", out var app) && app.GetString() is { Length: > 0 } a
                            ? $" (only in {a})"
                            : "";
                        lines.Add($"{from}  ->  {(to.Length == 0 ? "nothing" : to)}{where}");
                    }
                }
            }
        }

        return new Check("PowerToys Keyboard Manager",
            lines.Count == 0 ? "installed, but nothing is remapped" : string.Join("\n", lines), lines.Count > 0);
    }

    private static string Combination(JsonElement entry, string property)
    {
        if (!entry.TryGetProperty(property, out var value)) return "";
        string raw = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ToString();
        var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries)
                       .Select(p => uint.TryParse(p.Trim(), out uint vk) ? Keys.Label(vk) : p.Trim());
        return string.Join(" + ", parts);
    }

    /// Anything besides kbdclass in the keyboard class UpperFilters is a third-party driver
    /// sitting under every hook, where no user-mode tool can watch what it does.
    private static Check FilterDrivers()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e96b-e325-11ce-bfc1-08002be10318}");

        if (key?.GetValue("UpperFilters") is not string[] filters)
            return new Check("Keyboard filter drivers", "none registered", false);

        var extra = filters.Where(f => !f.Equals("kbdclass", StringComparison.OrdinalIgnoreCase)).ToList();
        return extra.Count == 0
            ? new Check("Keyboard filter drivers", "only kbdclass, the Windows default", false)
            : new Check("Keyboard filter drivers",
                string.Join(Environment.NewLine, extra) + Environment.NewLine + "third-party, and it sits below every hook", true);
    }

    // ---------- accessibility ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct STICKYKEYS { public uint Size, Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILTERKEYS { public uint Size, Wait, Delay, Repeat, Bounce, Flags; }

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfoW(uint action, uint param, ref STICKYKEYS data, uint write);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfoW(uint action, uint param, ref FILTERKEYS data, uint write);

    /// Filter Keys drops keystrokes on purpose, and it is switched on by holding right Shift
    /// for eight seconds - so people turn it on without meaning to.
    private static Check Accessibility()
    {
        var sticky = new STICKYKEYS { Size = (uint)Marshal.SizeOf<STICKYKEYS>() };
        SystemParametersInfoW(0x003A, sticky.Size, ref sticky, 0);

        var filter = new FILTERKEYS { Size = (uint)Marshal.SizeOf<FILTERKEYS>() };
        SystemParametersInfoW(0x0032, filter.Size, ref filter, 0);

        bool stickyOn = (sticky.Flags & 1) != 0;
        bool filterOn = (filter.Flags & 1) != 0;

        return new Check("Accessibility",
            $"Sticky Keys {(stickyOn ? "ON" : "off")}, Filter Keys {(filterOn ? "ON - it drops keystrokes deliberately" : "off")}",
            stickyOn || filterOn);
    }
}

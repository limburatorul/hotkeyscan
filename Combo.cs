using System.Text;

namespace HotkeyScan;

/// Modifier bits, deliberately equal to the MOD_* values RegisterHotKey expects.
[Flags]
internal enum KeyMods { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }

/// A hotkey: modifiers plus a virtual-key code.
internal readonly record struct Combo(KeyMods Mods, uint Vk)
{
    public override string ToString()
    {
        var sb = new StringBuilder();
        if ((Mods & KeyMods.Win) != 0) sb.Append("Win+");
        if ((Mods & KeyMods.Ctrl) != 0) sb.Append("Ctrl+");
        if ((Mods & KeyMods.Alt) != 0) sb.Append("Alt+");
        if ((Mods & KeyMods.Shift) != 0) sb.Append("Shift+");
        sb.Append(Keys.Name(Vk));
        return sb.ToString();
    }
}

internal static class Keys
{
    private static readonly Dictionary<string, uint> ByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<uint, string> ByVk = new();

    /// Keys that carry the extended-key bit in a keyboard scan; SendInput needs it or the
    /// wrong key arrives (arrows turn into numpad digits, and Win never fires at all).
    private static readonly HashSet<uint> Extended = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E, 0x5B, 0x5C, 0x6F, 0x90,
        0xA3, 0xA5, 0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3,
    };

    static Keys()
    {
        for (uint c = 'A'; c <= 'Z'; c++) Add(((char)c).ToString(), c);
        for (uint c = '0'; c <= '9'; c++) Add(((char)c).ToString(), c);
        for (uint i = 1; i <= 24; i++) Add("F" + i, 0x6F + i);
        for (uint i = 0; i <= 9; i++) Add("Num" + i, 0x60 + i);

        Add("Space", 0x20); Add("Tab", 0x09); Add("Enter", 0x0D); Add("Esc", 0x1B); Add("Backspace", 0x08);
        Add("Insert", 0x2D); Add("Delete", 0x2E); Add("Home", 0x24); Add("End", 0x23);
        Add("PageUp", 0x21); Add("PageDown", 0x22);
        Add("Left", 0x25); Add("Up", 0x26); Add("Right", 0x27); Add("Down", 0x28);
        Add("PrintScreen", 0x2C); Add("Pause", 0x13); Add("ScrollLock", 0x91);
        Add("CapsLock", 0x14); Add("NumLock", 0x90); Add("Apps", 0x5D);
        Add("NumMul", 0x6A); Add("NumAdd", 0x6B); Add("NumSub", 0x6D); Add("NumDec", 0x6E); Add("NumDiv", 0x6F);
        Add(";", 0xBA); Add("=", 0xBB); Add(",", 0xBC); Add("-", 0xBD); Add(".", 0xBE); Add("/", 0xBF);
        Add("`", 0xC0); Add("[", 0xDB); Add("\\", 0xDC); Add("]", 0xDD); Add("'", 0xDE); Add("OEM102", 0xE2);
        Add("VolumeMute", 0xAD); Add("VolumeDown", 0xAE); Add("VolumeUp", 0xAF);
        Add("MediaNext", 0xB0); Add("MediaPrev", 0xB1); Add("MediaStop", 0xB2); Add("MediaPlayPause", 0xB3);

        // Spellings people actually type, mapped onto the canonical names above.
        Alias("escape", 0x1B); Alias("ins", 0x2D); Alias("del", 0x2E); Alias("pgup", 0x21);
        Alias("pgdn", 0x22); Alias("pagedn", 0x22); Alias("return", 0x0D); Alias("bksp", 0x08);
        Alias("prtsc", 0x2C); Alias("prtscn", 0x2C); Alias("printscrn", 0x2C); Alias("snapshot", 0x2C);
        Alias("print", 0x2C); Alias("pgdown", 0x22); Alias("appskey", 0x5D);
        for (uint i = 0; i <= 9; i++) Alias("numpad" + i, 0x60 + i);
        Alias("backtick", 0xC0); Alias("tilde", 0xC0); Alias("grave", 0xC0);
        Alias("plus", 0xBB); Alias("minus", 0xBD); Alias("comma", 0xBC); Alias("period", 0xBE);
        Alias("slash", 0xBF); Alias("backslash", 0xDC); Alias("semicolon", 0xBA); Alias("quote", 0xDE);
        Alias("spacebar", 0x20); Alias("capital", 0x14); Alias("menu", 0x5D);
    }

    private static void Add(string name, uint vk)
    {
        ByName[name] = vk;
        ByVk[vk] = name;
    }

    private static void Alias(string name, uint vk) => ByName[name] = vk;

    public static string Name(uint vk) => ByVk.TryGetValue(vk, out var n) ? n : $"VK_{vk:X2}";

    private static readonly Dictionary<uint, string> Sided = new()
    {
        [0x10] = "Shift", [0x11] = "Ctrl", [0x12] = "Alt",
        [0xA0] = "Left Shift", [0xA1] = "Right Shift",
        [0xA2] = "Left Ctrl", [0xA3] = "Right Ctrl",
        [0xA4] = "Left Alt", [0xA5] = "Right Alt",
        [0x5B] = "Left Win", [0x5C] = "Right Win",
    };

    /// Like Name, but tells the two sides of a modifier apart - the whole question when only
    /// one of them misbehaves.
    public static string Label(uint vk) => Sided.TryGetValue(vk, out var n) ? n : Name(vk);

    public static bool TryParse(string text, out Combo combo)
    {
        combo = default;
        var mods = KeyMods.None;
        uint vk = 0;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "win" or "lwin" or "rwin" or "meta" or "super" or "cmd": mods |= KeyMods.Win; continue;
                case "ctrl" or "control" or "ctl": mods |= KeyMods.Ctrl; continue;
                case "alt": mods |= KeyMods.Alt; continue;
                case "shift": mods |= KeyMods.Shift; continue;
            }
            if (vk != 0 || !ByName.TryGetValue(raw, out vk)) return false;
        }

        if (vk == 0) return false;
        combo = new Combo(mods, vk);
        return true;
    }

    public static bool IsExtended(uint vk) => Extended.Contains(vk);

    /// The combinations worth sweeping: what an app would plausibly claim, and what a
    /// user would plausibly want for themselves.
    public static IEnumerable<Combo> Sweep()
    {
        KeyMods[] sets =
        {
            KeyMods.Ctrl | KeyMods.Alt,
            KeyMods.Ctrl | KeyMods.Shift,
            KeyMods.Alt | KeyMods.Shift,
            KeyMods.Ctrl | KeyMods.Alt | KeyMods.Shift,
            KeyMods.Win,
            KeyMods.Win | KeyMods.Shift,
            KeyMods.Win | KeyMods.Ctrl,
            KeyMods.Win | KeyMods.Alt,
            KeyMods.Win | KeyMods.Ctrl | KeyMods.Shift,
            KeyMods.Win | KeyMods.Alt | KeyMods.Shift,
        };

        var keys = new List<uint>();
        for (uint c = 'A'; c <= 'Z'; c++) keys.Add(c);
        for (uint c = '0'; c <= '9'; c++) keys.Add(c);
        for (uint i = 1; i <= 12; i++) keys.Add(0x6F + i);
        keys.AddRange(new uint[]
        {
            0x20, 0x0D, 0x09, 0x1B, 0x2D, 0x2E, 0x24, 0x23, 0x21, 0x22,
            0x25, 0x26, 0x27, 0x28, 0x2C, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE,
        });

        foreach (var m in sets)
            foreach (var k in keys)
                yield return new Combo(m, k);

        // Keys that get claimed bare, without a modifier.
        foreach (uint k in new uint[] { 0x2C, 0x13, 0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3 })
            yield return new Combo(KeyMods.None, k);
        for (uint i = 13; i <= 24; i++)
            yield return new Combo(KeyMods.None, 0x6F + i);
    }
}

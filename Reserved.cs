namespace HotkeyScan;

/// The shortcuts Windows itself holds.
internal static class Reserved
{
    /// System shortcuts. The kernel refuses these to everyone, so without this table they
    /// would all be reported as "taken by an unknown owner".
    private static readonly (string Combo, string What)[] Table =
    {
        ("Win+L", "lock the PC"),
        ("Win+D", "show the desktop"),
        ("Win+E", "File Explorer"),
        ("Win+R", "Run dialog"),
        ("Win+I", "Settings"),
        ("Win+X", "power user menu"),
        ("Win+A", "Quick Settings"),
        ("Win+N", "notification centre"),
        ("Win+S", "Search"),
        ("Win+Q", "Search"),
        ("Win+V", "clipboard history"),
        ("Win+G", "Xbox Game Bar"),
        ("Win+P", "display projection"),
        ("Win+H", "voice typing"),
        ("Win+K", "Cast"),
        ("Win+U", "accessibility settings"),
        ("Win+W", "widgets"),
        ("Win+B", "focus the notification area"),
        ("Win+M", "minimise everything"),
        ("Win+T", "cycle taskbar buttons"),
        ("Win+C", "Copilot"),
        ("Win+F", "Feedback Hub"),
        ("Win+O", "lock screen orientation"),
        ("Win+Z", "snap layouts"),
        ("Win+Tab", "Task View"),
        ("Win+Space", "switch keyboard layout"),
        ("Win+Home", "minimise all but the active window"),
        ("Win+.", "emoji panel"),
        ("Win+;", "emoji panel"),
        ("Win+,", "peek at the desktop"),
        ("Win+=", "Magnifier"),
        ("Win+-", "Magnifier"),
        ("Win+Esc", "close Magnifier"),
        ("Win+PrintScreen", "screenshot to file"),
        ("Win+Up", "maximise"),
        ("Win+Down", "restore or minimise"),
        ("Win+Left", "snap left"),
        ("Win+Right", "snap right"),
        ("Win+Shift+S", "Snipping Tool"),
        ("Win+Shift+M", "restore minimised windows"),
        ("Win+Shift+Up", "stretch vertically"),
        ("Win+Shift+Left", "move to the left monitor"),
        ("Win+Shift+Right", "move to the right monitor"),
        ("Win+Ctrl+D", "new virtual desktop"),
        ("Win+Ctrl+F4", "close the virtual desktop"),
        ("Win+Ctrl+Left", "previous virtual desktop"),
        ("Win+Ctrl+Right", "next virtual desktop"),
        ("Win+Ctrl+Enter", "Narrator"),
        ("Win+Ctrl+Q", "Quick Assist"),
        ("Win+Ctrl+Shift+B", "restart the graphics driver"),
        ("Win+Alt+R", "Game Bar: record"),
        ("Win+Alt+G", "Game Bar: record the last 30 seconds"),
        ("Win+Alt+PrintScreen", "Game Bar: screenshot"),
        ("Win+Shift+T", "cycle taskbar buttons backwards"),
        ("Win+Shift+Down", "restore or minimise vertically"),
        ("Win+Shift+Enter", "fullscreen for a Store app"),
        ("Win+Ctrl+C", "colour filters"),
        ("Win+Ctrl+N", "Narrator settings"),
        ("Win+Ctrl+O", "on-screen keyboard"),
        ("Win+Ctrl+S", "Windows Speech Recognition"),
        ("Win+Ctrl+F", "find computers on the domain"),
        ("Win+F1", "Windows help"),
        ("Win+Enter", "Narrator, on older Windows"),
        ("Win+Ctrl+Space", "previous input method"),
        ("Ctrl+Shift+Esc", "Task Manager"),
        ("Ctrl+Alt+Delete", "the security screen"),
        ("Ctrl+Alt+Tab", "sticky app switcher"),
        ("Alt+Shift+Tab", "switch apps backwards"),
        ("Ctrl+Alt+Shift+Tab", "sticky app switcher, backwards"),
    };

    /// The shell claims every digit against every modifier set, which is a third of what
    /// a sweep finds taken - spelling all fifty out by hand would only hide the rest.
    private static readonly (KeyMods Mods, string What)[] TaskbarDigits =
    {
        (KeyMods.Win, "launch taskbar item {0}"),
        (KeyMods.Win | KeyMods.Shift, "new instance of taskbar item {0}"),
        (KeyMods.Win | KeyMods.Ctrl, "last active window of taskbar item {0}"),
        (KeyMods.Win | KeyMods.Alt, "jump list of taskbar item {0}"),
        (KeyMods.Win | KeyMods.Ctrl | KeyMods.Shift, "taskbar item {0} as administrator"),
    };

    private static readonly Dictionary<Combo, string> Map = Build();

    private static Dictionary<Combo, string> Build()
    {
        var map = new Dictionary<Combo, string>();
        foreach (var (text, what) in Table)
            if (Keys.TryParse(text, out var combo)) map[combo] = what;

        foreach (var (mods, what) in TaskbarDigits)
            for (uint d = '0'; d <= '9'; d++)
                map[new Combo(mods, d)] = string.Format(what, d == '0' ? 10 : d - '0');

        return map;
    }

    public static bool TryGet(Combo combo, out string what) => Map.TryGetValue(combo, out what!);
}

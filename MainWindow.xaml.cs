using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace HotkeyScan;

public record Verdict(string Label, string Text, Brush Paint);
public record Row(string Hotkey, string Owner, bool Unknown);
public record AppRow(string Name, string Mechanism, string Defaults, Brush Paint);

/// The one place colours are decided, shared by both windows.
internal static class Paints
{
    public static readonly Brush Good = Frozen(0x18, 0x7A, 0x3E);
    public static readonly Brush Warn = Frozen(0xB0, 0x5A, 0x0B);
    public static readonly Brush Bad = Frozen(0xC0, 0x3A, 0x2B);
    public static readonly Brush Dim = Frozen(0x71, 0x76, 0x7E);
    public static readonly Brush Ink = Frozen(0x1B, 0x1D, 0x21);
    public static readonly Brush Lit = Frozen(0x2C, 0x5F, 0xE0);
    public static readonly Brush Idle = Frozen(0xEE, 0xF0, 0xF3);
    public static readonly Brush White = Frozen(0xFF, 0xFF, 0xFF);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Verdict> verdicts = new();
    private readonly ObservableCollection<Row> shown = new();
    private readonly List<Row> all = new();

    private List<Declared> declared = new();
    private List<KnownApp> apps = new();
    private Combo? current;

    /// Set while the empirical test injects keys, so the capture box ignores its own keystrokes.
    private bool injecting;

    public MainWindow()
    {
        InitializeComponent();
        VerdictList.ItemsSource = verdicts;
        Results.ItemsSource = shown;
        Loaded += Load;
    }

    private async void Load(object sender, RoutedEventArgs e)
    {
        Status.Text = "reading the configs of running apps…";
        var found = await Task.Run(() => (Apps: AppScan.RunningKnownApps(), Declared: AppScan.DeclaredHotkeys()));

        apps = found.Apps;
        declared = found.Declared;
        AppsList.ItemsSource = apps.Select(a => new AppRow(
            a.Name,
            a.Mechanism switch
            {
                "hook" => "keyboard hook - invisible to the probe",
                "system" => "built into Windows",
                _ => "reserves them in the kernel - the probe sees these",
            },
            a.Defaults,
            a.Mechanism == "hook" ? Paints.Warn : Paints.Dim));

        Status.Text = $"{apps.Count} known apps running, {declared.Count} hotkeys read out of their configs.";
        HotkeyBox.Focus();
    }

    // ---------- the combination ----------

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (injecting) { e.Handled = true; return; }

        var mods = KeyMods.None;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= KeyMods.Ctrl;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= KeyMods.Alt;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= KeyMods.Shift;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= KeyMods.Win;

        // With no Ctrl, Alt or Win held the box stays an ordinary text field, so a combination
        // Windows eats before we ever see it can still be typed by hand.
        if ((mods & (KeyMods.Ctrl | KeyMods.Alt | KeyMods.Win)) == 0) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        e.Handled = true;
        HotkeyBox.Text = new Combo(mods, vk).ToString();
        HotkeyBox.CaretIndex = HotkeyBox.Text.Length;
    }

    private void HotkeyBox_TextChanged(object sender, RoutedEventArgs e)
    {
        current = Keys.TryParse(HotkeyBox.Text, out var combo) ? combo : null;
        CheckButton.IsEnabled = PressButton.IsEnabled = current != null;
    }

    // ---------- verdict ----------

    private void Check_Click(object sender, RoutedEventArgs e)
    {
        if (current is not Combo combo) return;

        verdicts.Clear();
        VerdictCard.Visibility = Visibility.Visible;

        verdicts.Add(Win32.TryClaim(combo, out int error) switch
        {
            Probe.Free => new Verdict("kernel probe", "free - nothing holds it through RegisterHotKey", Paints.Good),
            Probe.Taken => new Verdict("kernel probe", "TAKEN - another process has reserved it", Paints.Warn),
            _ => new Verdict("kernel probe", $"could not be tested (error {error})", Paints.Bad),
        });

        verdicts.Add(Reserved.TryGet(combo, out var what)
            ? new Verdict("Windows", "system shortcut: " + what, Paints.Warn)
            : new Verdict("Windows", "not a system shortcut", Paints.Dim));

        var matches = declared.Where(d => d.Combo == combo).ToList();
        if (matches.Count == 0)
            verdicts.Add(new Verdict("app configs", "no match in PowerToys, ShareX, OBS or AutoHotkey", Paints.Dim));
        else
            foreach (var d in matches)
                verdicts.Add(new Verdict("app configs", $"{d.App} - {d.What}\n{d.Source}", Paints.Warn));

        var hooks = apps.Where(a => a.Mechanism == "hook").Select(a => a.Name).ToList();
        if (hooks.Count > 0)
            verdicts.Add(new Verdict("hook suspects",
                string.Join(", ", hooks) + " - they take keys without reserving them, so only \"Press it\" can catch them", Paints.Dim));
    }

    /// The only way to catch a keyboard hook: press the combination and watch what moves.
    private async void Press_Click(object sender, RoutedEventArgs e)
    {
        if (current is not Combo combo) return;

        var go = MessageBox.Show(this,
            $"This presses {combo} for real - whatever owns it will actually fire.\n\nContinue?",
            "Empirical test", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (go != MessageBoxResult.OK) return;

        PressButton.IsEnabled = false;
        Status.Text = $"pressing {combo}…";

        var before = Win32.Foreground();
        var windowsBefore = Win32.VisibleWindows();
        var processesBefore = Process.GetProcesses().Select(p => p.Id).ToHashSet();
        uint clipboardBefore = Win32.ClipboardSequence();

        injecting = true;
        Win32.Press(combo);

        Win32.WindowInfo? moved = null;
        for (int i = 0; i < 12 && moved == null; i++)
        {
            await Task.Delay(150);
            var now = Win32.Foreground();
            if (now != null && now.Handle != before?.Handle) moved = now;
        }
        await Task.Delay(300);
        injecting = false;

        foreach (var stale in verdicts.Where(v => v.Label == "pressed it").ToList())
            verdicts.Remove(stale);

        VerdictCard.Visibility = Visibility.Visible;
        bool anything = false;

        if (moved != null)
        {
            verdicts.Add(new Verdict("pressed it", $"focus moved to {moved.Process} - {moved.Title}", Paints.Good));
            anything = true;
        }

        foreach (var (handle, info) in Win32.VisibleWindows())
        {
            if (windowsBefore.ContainsKey(handle)) continue;
            verdicts.Add(new Verdict("pressed it", $"new window from {info.Process} - {info.Title}", Paints.Good));
            anything = true;
        }

        foreach (var p in Process.GetProcesses())
        {
            if (processesBefore.Contains(p.Id)) continue;
            verdicts.Add(new Verdict("pressed it", "new process: " + p.ProcessName, Paints.Good));
            anything = true;
        }

        if (Win32.ClipboardSequence() != clipboardBefore)
        {
            verdicts.Add(new Verdict("pressed it", "the clipboard changed - a capture or clipboard tool answered", Paints.Good));
            anything = true;
        }

        if (!anything)
            verdicts.Add(new Verdict("pressed it",
                "nothing moved. Either nothing owns it, or the owner acts silently: a mute, a macro, an overlay drawn inside a game.", Paints.Dim));

        PressButton.IsEnabled = true;
        Status.Text = "";
    }

    // ---------- sweep ----------

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        var combos = Keys.Sweep().ToList();
        Status.Text = $"probing {combos.Count} combinations…";

        var result = await Task.Run(() =>
        {
            var taken = new HashSet<Combo>();
            foreach (var combo in combos)
                if (Win32.TryClaim(combo, out _) == Probe.Taken) taken.Add(combo);
            return (Taken: taken, Declared: AppScan.DeclaredHotkeys());
        });

        declared = result.Declared;
        var byCombo = declared.GroupBy(d => d.Combo).ToDictionary(g => g.Key, g => g.First());

        all.Clear();
        int unknown = 0;
        foreach (var combo in combos)
        {
            if (!result.Taken.Contains(combo)) continue;

            if (Reserved.TryGet(combo, out var what))
                all.Add(new Row(combo.ToString(), "Windows - " + what, false));
            else if (byCombo.TryGetValue(combo, out var d))
                all.Add(new Row(combo.ToString(), $"{d.App} - {d.What}", false));
            else
            {
                all.Add(new Row(combo.ToString(), "owner unknown", true));
                unknown++;
            }
        }

        foreach (var (combo, d) in byCombo)
            if (!result.Taken.Contains(combo))
                all.Add(new Row(combo.ToString(), $"{d.App} - {d.What}  (keyboard hook, nothing reserved)", false));

        ApplyFilter();
        ScanButton.IsEnabled = true;
        Status.Text = $"{result.Taken.Count} of {combos.Count} combinations are taken, {unknown} of them by an owner Windows will not name. "
            + "Double-click a row to load it, then \"Press it\" to see who answers.";
    }

    private KeyMonitor? monitor;

    /// One monitor at a time: each one installs a keyboard hook.
    private void Monitor_Click(object sender, RoutedEventArgs e)
    {
        if (monitor is { IsVisible: true }) { monitor.Activate(); return; }
        monitor = new KeyMonitor { Owner = this };
        monitor.Show();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (FilterBox == null || OnlyUnknown == null) return;

        string needle = FilterBox.Text.Trim();
        shown.Clear();
        foreach (var row in all)
        {
            if (OnlyUnknown.IsChecked == true && !row.Unknown) continue;
            if (needle.Length > 0
                && !row.Hotkey.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !row.Owner.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            shown.Add(row);
        }
    }

    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Results.SelectedItem is not Row row) return;
        HotkeyBox.Text = row.Hotkey;
        Check_Click(sender, e);
    }
}

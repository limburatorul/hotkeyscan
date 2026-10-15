using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
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

    /// Set instead of current for a double-tap binding ("double-tap Win"): only Press it can test those.
    private uint tapVk;

    /// Set while the empirical test injects keys, so the capture box ignores its own keystrokes.
    private bool injecting;

    public MainWindow()
    {
        InitializeComponent();
        VerdictList.ItemsSource = verdicts;
        Results.ItemsSource = shown;
        Loaded += Load;
        Task.Run(Ecosystem.WriteCard);
        Loaded += async (_, _) => await CheckForUpdate();
        // the app may stay open for days: a release published meanwhile is fetched too
        var again = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        again.Tick += async (_, _) => await CheckForUpdate();
        again.Start();
    }

    private async Task CheckForUpdate()
    {
        if (await Updater.UpdateInPlace() is not { } version) return;
        UpdateLine.Text = $"HotkeyScan {version} is downloaded and checked. It starts the next time you open HotkeyScan.";
        UpdateLine.Visibility = Visibility.Visible;
    }

    private void ShowOffer() => OfferLine.Visibility = Visibility.Visible;

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

        // HotkeyScan --check "Alt+Space": a sister app couldn't register that combination, so the
        // question is who holds it - check at once and point at Find the owner.
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "--check");
        if (at > 0 && at + 1 < args.Length)
        {
            HotkeyBox.Text = args[at + 1].Trim();
            if (current is Combo combo)
            {
                Check_Click(this, e);
                FindButton.Style = (Style)FindResource("Primary");
                CheckButton.ClearValue(StyleProperty);
                FindButton.Focus();
                Status.Text = $"Another app couldn't register {combo}. Find the owner names the program holding it.";
            }
        }

        var cards = await Task.Run(() => Ecosystem.Sisters.Concat(Ecosystem.Paid).DistinctBy(s => s.Id)
            .Select(s => (s.Id, Card: Ecosystem.ReadCard(s.Id))).Where(c => c.Card != null).ToDictionary(c => c.Id, c => c.Card!));
        ShowWorksWith(cards);
    }

    /// The sisters that send their taken hotkeys here, and, for someone who bought one of our paid apps,
    /// one quiet line with 30% off the paid ones not installed.
    private void ShowWorksWith(Dictionary<string, Ecosystem.Card> cards)
    {
        Inline Link(string name, string url)
        {
            var link = new Hyperlink(new Run(name)) { NavigateUri = new Uri(url) };
            link.RequestNavigate += Link_Navigate;
            return link;
        }

        var lines = new List<List<Inline>>();
        foreach (var (id, name) in Ecosystem.Sisters.Where(s => cards.ContainsKey(s.Id)))
            lines.Add(new() { new Run(name), new Run(" · sends a hotkey it can't register here") });
        foreach (var (id, name) in Ecosystem.Sisters.Where(s => !cards.ContainsKey(s.Id)).Take(3))
            lines.Add(new() { Link(name, Ecosystem.Page(id, null)), new Run(" · would send a hotkey it can't register here") });

        if (cards.Values.Any(c => c.Paid && c.Edition == "full"))
        {
            var offers = Ecosystem.Paid.Where(p => !cards.ContainsKey(p.Id)).Take(4).ToList();
            if (offers.Count > 0)
            {
                var line = new List<Inline> { new Run("Owners of one of our apps get 30% off the others: ") };
                for (int i = 0; i < offers.Count; i++)
                {
                    if (i > 0) line.Add(new Run(" · "));
                    line.Add(Link(offers[i].Name, Ecosystem.Page(offers[i].Id, offers[i].Id == "shelf" ? "SECOND30SHELF" : "SECOND30")));
                }
                lines.Add(line);
            }
        }

        WorksText.Inlines.Clear();
        foreach (var line in lines)
        {
            if (WorksText.Inlines.Count > 0) WorksText.Inlines.Add(new LineBreak());
            WorksText.Inlines.AddRange(line);
        }
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
        var tap = Regex.Match(HotkeyBox.Text.Trim(), @"^double-tap (win|ctrl|alt|shift)$", RegexOptions.IgnoreCase);
        tapVk = !tap.Success ? 0 : tap.Groups[1].Value.ToLowerInvariant() switch { "win" => 0x5Bu, "ctrl" => 0x11u, "alt" => 0x12u, _ => 0x10u };
        CheckButton.IsEnabled = FindButton.IsEnabled = current != null;
        PressButton.IsEnabled = current != null || tapVk != 0;

        // A verdict belongs to the combination it was made for.
        verdicts.Clear();
        VerdictCard.Visibility = TellButton.Visibility = Visibility.Collapsed;
    }

    // ---------- verdict ----------

    private void Check_Click(object sender, RoutedEventArgs e)
    {
        if (current is not Combo combo) return;
        ShowOffer();

        verdicts.Clear();
        VerdictCard.Visibility = Visibility.Visible;
        TellButton.Visibility = Visibility.Collapsed;

        var probe = Win32.TryClaim(combo, out int error);
        verdicts.Add(probe switch
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
            verdicts.Add(new Verdict("app configs", "no match in PowerToys, ShareX, OBS, AutoHotkey or Raycast", Paints.Dim));
        else
            foreach (var d in matches)
                verdicts.Add(new Verdict("app configs", $"{d.App} - {d.What}\n{d.Source}", Paints.Warn));

        if (probe == Probe.Taken && !Reserved.TryGet(combo, out _) && matches.Count == 0)
        {
            verdicts.Add(new Verdict("owner", "unknown - \"Find the owner\" can name the program that reserved it", Paints.Warn));
            TellButton.Visibility = Visibility.Visible;
        }

        var hooks = apps.Where(a => a.Mechanism == "hook").Select(a => a.Name).ToList();
        if (hooks.Count > 0)
            verdicts.Add(new Verdict("hook suspects",
                string.Join(", ", hooks) + " - they take keys without reserving them, so only \"Press it\" can catch them", Paints.Dim));
    }

    /// The only way to catch a keyboard hook: press the combination and watch what moves.
    private async void Press_Click(object sender, RoutedEventArgs e)
    {
        string what;
        if (current is Combo c) what = c.ToString();
        else if (tapVk != 0) what = HotkeyBox.Text.Trim();
        else return;

        var go = MessageBox.Show(this,
            $"This presses {what} for real - whatever owns it will actually fire.\n\nContinue?",
            "Empirical test", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (go != MessageBoxResult.OK) return;

        PressButton.IsEnabled = false;
        Status.Text = $"pressing {what}…";

        int self = Environment.ProcessId;
        var before = Win32.Foreground();
        IntPtr focusBefore = Win32.FocusedWindow();
        var windowsBefore = Win32.VisibleWindows();
        var processesBefore = Process.GetProcesses().Select(p => p.Id).ToHashSet();
        uint clipboardBefore = Win32.ClipboardSequence();

        injecting = true;
        if (current is Combo combo) Win32.Press(combo);
        else
        {
            Win32.Press(new Combo(KeyMods.None, tapVk));
            await Task.Delay(120);
            Win32.Press(new Combo(KeyMods.None, tapVk));
        }

        // About 1.5 s: long enough for a launcher to draw itself, short enough to still be its doing.
        Win32.WindowInfo? moved = null;
        IntPtr focused = IntPtr.Zero;
        for (int i = 0; i < 10 && moved == null; i++)
        {
            await Task.Delay(150);
            var now = Win32.Foreground();
            if (now != null && now.Handle != before?.Handle) moved = now;
            IntPtr f = Win32.FocusedWindow();
            if (f != IntPtr.Zero && f != focusBefore) focused = f;
        }
        injecting = false;

        foreach (var stale in verdicts.Where(v => v.Label == "pressed it").ToList())
            verdicts.Remove(stale);

        VerdictCard.Visibility = Visibility.Visible;
        var seen = new HashSet<string>();
        void Report(string how, Win32.WindowInfo w)
        {
            if (w.Pid == self) return;
            string line = $"{how} {w.Process} - {Win32.ExePath(w.Pid)}" + (w.Title.Length > 0 ? $"\n\"{w.Title}\"" : "");
            if (seen.Add(line)) verdicts.Add(new Verdict("pressed it", line, Paints.Good));
        }

        if (moved != null) Report("the foreground moved to", moved);
        foreach (var (handle, info) in Win32.VisibleWindows())
            if (!windowsBefore.ContainsKey(handle)) Report("a new window from", info);
        if (moved == null && focused != IntPtr.Zero) Report("focus went to", Win32.Describe(focused));
        bool anything = seen.Count > 0;

        foreach (var p in Process.GetProcesses())
        {
            if (processesBefore.Contains(p.Id)) continue;
            verdicts.Add(new Verdict("pressed it", $"new process: {p.ProcessName} - {Win32.ExePath((uint)p.Id)}", Paints.Good));
            anything = true;
        }

        if (Win32.ClipboardSequence() != clipboardBefore)
        {
            verdicts.Add(new Verdict("pressed it", "the clipboard changed - a capture or clipboard tool answered", Paints.Good));
            anything = true;
        }

        if (anything) TellButton.Visibility = Visibility.Collapsed;
        else
        {
            verdicts.Add(new Verdict("pressed it",
                "nothing reacted: held by a keyboard hook or nothing; owner unknown. A hook can also act silently (a mute, a macro, an overlay inside a game).", Paints.Warn));
            TellButton.Visibility = Visibility.Visible;
        }

        PressButton.IsEnabled = true;
        Status.Text = "";
    }

    /// Names whoever registered the combination, by catching the WM_HOTKEY Windows posts to it.
    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        if (current is not Combo combo) return;
        const int seconds = 10;

        var go = MessageBox.Show(this,
            $"To see who receives {combo}, HotkeyScan briefly loads a small hook DLL into running programs "
            + $"for up to {seconds} seconds, then removes it. Nothing leaves this PC.\n\n"
            + "Games with anti-cheat may object to that - close games first.\n\n"
            + $"After you click OK, press {combo} yourself.",
            "Find the owner", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (go != MessageBoxResult.OK) return;

        foreach (var stale in verdicts.Where(v => v.Label == "owner").ToList())
            verdicts.Remove(stale);
        VerdictCard.Visibility = Visibility.Visible;
        FindButton.IsEnabled = PressButton.IsEnabled = CheckButton.IsEnabled = false;
        injecting = true;   // the capture box must not rewrite itself while the user presses
        Status.Text = $"Press {combo} now - {seconds} s";

        try
        {
            var hit = await OwnerFinder.Find(combo, seconds);
            if (hit != null)
            {
                verdicts.Add(new Verdict("owner", $"{hit.Process} (pid {hit.Pid}) - {hit.Path}"
                    + (hit.Title.Length > 0 ? $"\n\"{hit.Title}\"" : ""), Paints.Good));
                TellButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
                verdicts.Add(new Verdict("owner",
                    $"nothing caught in {seconds} s. It wasn't pressed, nothing reserved it (a keyboard hook - try Press it), "
                    + (admin ? "or the owner never reads its message queue."
                             : "or the owner runs as administrator: those are only reachable when HotkeyScan runs as administrator too."),
                    Paints.Warn));
                TellButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            verdicts.Add(new Verdict("owner", "could not run: " + ex.Message, Paints.Bad));
        }
        finally
        {
            injecting = false;
            FindButton.IsEnabled = PressButton.IsEnabled = CheckButton.IsEnabled = true;
            Status.Text = "";
        }
    }

    /// Shows the whole report first so the user can cut lines; the mail client sends it, never HotkeyScan.
    private void Tell_Click(object sender, RoutedEventArgs e)
    {
        var names = Process.GetProcesses().Select(p => p.ProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        string body = $"Combination: {HotkeyBox.Text.Trim()}\n"
            + $"HotkeyScan: {typeof(App).Assembly.GetName().Version?.ToString(3)}\n"
            + $"Windows build: {Environment.OSVersion.Version}\n"
            + "What HotkeyScan said:\n" + string.Join("\n", verdicts.Select(v => $"  {v.Label}: {v.Text.Replace("\n", " ")}")) + "\n"
            + "Running processes (delete any you'd rather not share):\n" + string.Join("\n", names);

        var text = new TextBox
        {
            Text = body, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"), FontSize = 12,
        };
        AutomationProperties.SetName(text, "Report text");
        var note = new TextBlock
        {
            Text = "This is everything the e-mail will contain - delete any line you like. Your mail app opens with it; nothing is sent until you send it there.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Style = (Style)FindResource("Hint"),
        };
        var send = new Button { Content = "Open in my mail app", IsDefault = true, Style = (Style)FindResource("Primary") };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(send);
        buttons.Children.Add(cancel);
        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(note, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(note);
        layout.Children.Add(buttons);
        layout.Children.Add(text);

        var dialog = new Window
        {
            Title = "Tell us about an unknown owner", Owner = this, Width = 560, Height = 520, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)FindResource("Page"), Content = layout,
        };
        send.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true) return;

        string url = "mailto:feedback@protagonistlabs.app?subject=HotkeyScan%20unknown%20owner&body="
            + Uri.EscapeDataString(text.Text.ReplaceLineEndings("\r\n"));
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
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
        var byCombo = declared.Where(d => d.Text == null).GroupBy(d => d.Combo).ToDictionary(g => g.Key, g => g.First());

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
        foreach (var d in declared.Where(d => d.Text != null))
            all.Add(new Row(d.Text!, $"{d.App} - {d.What}  (keyboard hook - only \"Press it\" can confirm)", false));

        ApplyFilter();
        ScanButton.IsEnabled = true;
        Status.Text = $"{result.Taken.Count} of {combos.Count} combinations are taken, {unknown} of them by an owner Windows will not name. "
            + "Double-click a row to load it, then \"Press it\" to see who answers.";
        ShowOffer();
    }

    private KeyMonitor? monitor;

    private void Link_Navigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

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

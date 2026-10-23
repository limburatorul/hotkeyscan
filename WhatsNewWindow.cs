using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace HotkeyScan;

/// What's new, as the studio's other apps show it: every release in a list on the left, under the month it came out,
/// and the chosen one on the right under its 960 x 170 banner. The notes are whatsnew.txt, built into the exe, so it
/// works offline. It opens once after an update, and counts as seen when it is closed, not when it opens.
public sealed class WhatsNewWindow : Window
{
    internal sealed record Release(string Version, DateTime Date, string Title, List<string> Lines);

    private static WhatsNewWindow? open;
    private static string SeenFile => Path.Combine(Ecosystem.Data, "whatsnew-seen.txt");
    private static readonly Brush Selected = Frozen(Color.FromRgb(0xE3, 0xEA, 0xFB));

    private readonly List<Release> releases = Load();
    private readonly StackPanel nav = new();
    private readonly Border banner = new() { CornerRadius = new CornerRadius(8), ClipToBounds = true, Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel notes = new();
    private readonly ScrollViewer notesScroll;
    private int shown = -1;

    /// Brings the window up, or to the front when it is already open.
    public static void Open(Window owner)
    {
        if (open is not null) { open.Activate(); return; }
        open = new WhatsNewWindow { Owner = owner };
        open.Closed += (_, _) => { open = null; MarkSeen(); };
        open.Show();
    }

    /// True once after an update. HotkeyScan kept nothing before 1.2, so for a copy that never wrote down a version the
    /// card it wrote at its last start says whether it ran before: no card is a first run, which has nothing new to tell.
    public static bool DueAfterUpdate(string? previousVersion)
    {
        string? seen = null;
        try { seen = File.ReadAllText(SeenFile).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (seen == Updater.Current.ToString()) return false;
        if (seen is null && previousVersion is null) { MarkSeen(); return false; }
        return true;
    }

    private static void MarkSeen()
    {
        try
        {
            Directory.CreateDirectory(Ecosystem.Data);
            File.WriteAllText(SeenFile, Updater.Current.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // shown again next time, no harm
    }

    internal static List<Release> Load()
    {
        using var stream = typeof(WhatsNewWindow).Assembly.GetManifestResourceStream("HotkeyScan.whatsnew.txt");
        using var reader = new StreamReader(stream!);
        var list = new List<Release>();
        foreach (var line in reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("## ") && line[3..].Split(" | ", 3) is [var v, var d, var t])
                list.Add(new Release(v.Trim(), DateTime.ParseExact(d.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture), t.Trim(), new()));
            else if (line.StartsWith("# ") || line.Trim() == "" || list.Count == 0) continue;     // comments, blank lines
            else list[^1].Lines.Add(line);
        }
        return list;
    }

    private WhatsNewWindow()
    {
        Title = "What's new — HotkeyScan";
        Width = 1240; Height = 720; MinWidth = 700; MinHeight = 420;     // room for the list and a 960 x 170 banner
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("Page");
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key is Key.Down or Key.Up) { Show(Math.Clamp(shown + (e.Key == Key.Down ? 1 : -1), 0, releases.Count - 1)); e.Handled = true; }
        };

        var close = new Button { Content = "Close", Style = (Style)FindResource("Primary"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), IsCancel = true };
        close.Click += (_, _) => Close();

        var navScroll = new ScrollViewer { Content = nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 200, Margin = new Thickness(0, 0, 16, 0) };
        notesScroll = new ScrollViewer { Content = notes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
        var right = new DockPanel();
        DockPanel.SetDock(banner, Dock.Top);
        right.Children.Add(banner);
        right.Children.Add(notesScroll);

        var body = new DockPanel { Margin = new Thickness(18, 16, 18, 14) };
        DockPanel.SetDock(close, Dock.Bottom);
        DockPanel.SetDock(navScroll, Dock.Left);
        body.Children.Add(close);
        body.Children.Add(navScroll);
        body.Children.Add(right);
        Content = body;

        BuildNav();
        // the version running, which is the newest unless this build is older than its own notes
        Show(Math.Max(0, releases.FindIndex(r => r.Version == $"{Updater.Current.Major}.{Updater.Current.Minor}")));
    }

    private void BuildNav()
    {
        string? month = null;
        for (int i = 0; i < releases.Count; i++)
        {
            var r = releases[i];
            var m = r.Date.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
            if (m != month)
            {
                month = m;
                nav.Children.Add(new TextBlock { Text = m, FontSize = 11, Foreground = Paints.Dim, Margin = new Thickness(8, nav.Children.Count == 0 ? 0 : 12, 0, 4) });
            }
            int index = i;
            var row = new DockPanel { Width = 166 };      // the button's template centres its content: the row spans it
            var when = new TextBlock { Text = i == 0 ? "Latest" : r.Date.ToString("d MMM", CultureInfo.InvariantCulture), FontSize = 11, Foreground = Paints.Dim, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(when, Dock.Right);
            row.Children.Add(when);
            row.Children.Add(new TextBlock { Text = r.Version, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = row, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Padding = new Thickness(0, 7, 0, 7), Margin = new Thickness(0, 1, 0, 1), ToolTip = r.Title };
            System.Windows.Automation.AutomationProperties.SetName(button, $"{r.Version}, {r.Title}");
            button.Click += (_, _) => Show(index);
            nav.Children.Add(button);
        }
    }

    private void Show(int i)
    {
        if (i == shown || i < 0 || i >= releases.Count) return;
        shown = i;
        var r = releases[i];
        var buttons = nav.Children.OfType<Button>().ToList();
        for (int b = 0; b < buttons.Count; b++) buttons[b].Background = b == i ? Selected : Brushes.Transparent;
        buttons[i].BringIntoView();

        banner.Child = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = Banner(r) };
        notes.Children.Clear();
        notes.Children.Add(new TextBlock { Text = r.Date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture), Foreground = Paints.Dim, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var line in r.Lines)
        {
            var text = line.Trim();
            if (text.StartsWith("- "))
            {
                var point = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var dot = new TextBlock { Text = "•", Foreground = Paints.Lit, Margin = new Thickness(2, 0, 10, 0) };
                DockPanel.SetDock(dot, Dock.Left);
                point.Children.Add(dot);
                point.Children.Add(Rich(text[2..]));
                notes.Children.Add(point);
            }
            else if (text.StartsWith("**") && text.EndsWith("**") && text.Length > 4 && !text[2..^2].Contains("**"))
                notes.Children.Add(new TextBlock { Text = text[2..^2], FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 12, 0, 4) });
            else
            {
                var p = Rich(text);
                p.Margin = new Thickness(0, 4, 0, 6);
                notes.Children.Add(p);
            }
        }
        notesScroll.ScrollToTop();
    }

    /// A line of the notes with its **bold** parts in bold.
    private static TextBlock Rich(string text)
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
        var parts = text.Split("**");
        for (int i = 0; i < parts.Length; i++)
            if (parts[i] != "") t.Inlines.Add(new Run(parts[i]) { FontWeight = i % 2 == 1 ? FontWeights.SemiBold : FontWeights.Normal });
        return t;
    }

    /// HotkeyScan's colours under the version and title: its blue over a dark ground, a row of keys running through it.
    private static UIElement Banner(Release r)
    {
        var ground = new LinearGradientBrush(Color.FromRgb(0x0E, 0x15, 0x2B), Color.FromRgb(0x14, 0x22, 0x4A), 20);
        var glow = new System.Windows.Shapes.Ellipse
        {
            Width = 560, Height = 380,
            Fill = new RadialGradientBrush(Color.FromArgb(0x66, 0x2C, 0x5F, 0xE0), Color.FromArgb(0, 0x2C, 0x5F, 0xE0)),
        };
        var art = new Canvas { Width = 960, Height = 170, ClipToBounds = true };
        Canvas.SetLeft(glow, 520); Canvas.SetTop(glow, -150);
        art.Children.Add(glow);
        var v = r.Version.Split('.').Select(int.Parse).ToArray();
        var rnd = new Random(v[0] * 100 + v[1]);     // seeded by the version: a release looks the same every time
        for (int x = 400; x < 940; x += 54)
        {
            bool lit = rnd.NextDouble() < 0.25;
            var key = new System.Windows.Shapes.Rectangle
            {
                Width = 44, Height = 44, RadiusX = 7, RadiusY = 7, StrokeThickness = 1.5,
                Stroke = new SolidColorBrush(Color.FromArgb(lit ? (byte)0xE0 : (byte)0x60, 0x7F, 0xA4, 0xFF)),
                Fill = new SolidColorBrush(Color.FromArgb(lit ? (byte)0x55 : (byte)0x10, 0x2C, 0x5F, 0xE0)),
            };
            Canvas.SetLeft(key, x); Canvas.SetTop(key, 63 + (rnd.NextDouble() - 0.5) * 30);
            art.Children.Add(key);
        }
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(26, 0, 0, 0), MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left };
        words.Children.Add(new TextBlock { Text = "WHAT’S NEW IN", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255)) });
        words.Children.Add(new TextBlock { Text = r.Version, FontSize = 34, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        words.Children.Add(new TextBlock { Text = r.Title, FontSize = 14, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255)) });
        return new Grid { Width = 960, Height = 170, Background = ground, Children = { art, words } };
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

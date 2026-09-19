using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace HotkeyScan;

public record LogRow(string Time, string Delta, string Source, string Event, string Detail, Brush Paint);
public record CheckRow(string Title, string Detail, Brush Paint);

/// One modifier, and whether it is held down at this instant.
public sealed class Chip : INotifyPropertyChanged
{
    private bool down;

    public Chip(string name, int vk)
    {
        Name = name;
        Vk = vk;
    }

    public string Name { get; }
    public int Vk { get; }

    public bool Down
    {
        get => down;
        set
        {
            if (down == value) return;
            down = value;
            Raise(nameof(Paint));
            Raise(nameof(Ink));
        }
    }

    public Brush Paint => down ? Paints.Lit : Paints.Idle;
    public Brush Ink => down ? Paints.White : Paints.Dim;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class KeyMonitor : Window
{
    private const int WmInput = 0x00FF;

    /// Both sides of every modifier, because the whole question is which side misbehaves.
    private static readonly uint[] Modifiers = { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C };

    private readonly ObservableCollection<LogRow> rows = new();
    private readonly List<Chip> chips = new()
    {
        new("Left Ctrl", 0xA2), new("Right Ctrl", 0xA3),
        new("Left Shift", 0xA0), new("Right Shift", 0xA1),
        new("Left Alt", 0xA4), new("Right Alt", 0xA5),
        new("Left Win", 0x5B), new("Right Win", 0x5C),
    };

    private readonly Dictionary<IntPtr, string> devices = new();
    private readonly Win32.KeyboardWatch watch;
    private readonly DispatcherTimer timer;

    // Both are touched from the hook thread as well as the UI thread.
    private volatile int watched = -1;
    private int hookEvents, rawEvents;
    private DateTime last = DateTime.MinValue;

    public KeyMonitor()
    {
        InitializeComponent();
        Log.ItemsSource = rows;
        Chips.ItemsSource = chips;
        Checks.ItemsSource = Remaps.All()
            .Select(c => new CheckRow(c.Title, c.Detail, c.Suspicious ? Paints.Warn : Paints.Dim));

        // Installed last, so this hook runs first and sees a key before another hook can eat it.
        watch = new Win32.KeyboardWatch(OnHookKey);
        if (!watch.Installed)
            WatchHint.Text = "The keyboard hook could not be installed, so the log only shows what this window itself receives.";

        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) =>
        {
            foreach (var chip in chips) chip.Down = (Win32.GetAsyncKeyState(chip.Vk) & 0x8000) != 0;
            HookStatus.Text = HookState();
        };
        timer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(WndProc);
        if (!Win32.ListenToRawKeyboard(source.Handle))
            WatchHint.Text += "  Raw input could not be started, so the \"raw\" rows will be missing.";
    }

    /// Security software that guards against keyloggers can leave a hook installed but starve it,
    /// and a hook silently seeing nothing would read as a bug rather than as the fact it is.
    private string HookState()
    {
        if (!watch.Installed) return "HOOK: COULD NOT BE INSTALLED";
        if (hookEvents > 0) return $"HOOK: {hookEvents} EVENTS SEEN";
        return rawEvents > 0 ? "HOOK: BLOCKED IN THIS PROCESS" : "HOOK: NOTHING YET";
    }

    /// Modifiers always, plus the one key under investigation. Logging everything would make
    /// this a keylogger, which is not what it is for.
    private bool Interesting(uint vk) => vk == (uint)watched || Modifiers.Contains(vk);

    // ---------- what the keyboard reports, below every hook ----------

    private IntPtr WndProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmInput) return IntPtr.Zero;

        var key = Win32.ReadRawKey(lParam);
        if (key == null) return IntPtr.Zero;
        rawEvents++;

        uint vk = Win32.SideOf(key);
        if (!Interesting(vk)) return IntPtr.Zero;

        Add("raw", key.Down, vk, $"scancode 0x{key.Scan:X2}{(key.E0 ? ", E0" : "")}, from {Device(key.Device)}", Paints.Good);
        return IntPtr.Zero;
    }

    /// One short label per physical keyboard, so an external one is obvious next to the built-in.
    private string Device(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return "no device (synthesized)";
        if (devices.TryGetValue(handle, out var known)) return known;

        string name = Win32.RawDeviceName(handle);
        var match = Regex.Match(name, @"VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4}|ACPI#[^#]+|PNP[0-9A-Fa-f]{4}");
        string label = $"keyboard {devices.Count + 1} ({(match.Success ? match.Value : name)})";
        devices[handle] = label;
        return label;
    }

    // ---------- what the hook chain sees ----------

    private void OnHookKey(Win32.KeyEvent e)
    {
        Interlocked.Increment(ref hookEvents);
        if (!Interesting(e.Vk)) return;

        string detail = $"vk 0x{e.Vk:X2}, scancode 0x{e.Scan:X2}";
        if (e.Extended) detail += ", extended";
        if (e.Injected) detail += e.LowIntegrity ? ", INJECTED by software (lower integrity)" : ", INJECTED by software";

        // Windows silently drops a low-level hook that takes too long to return, so the callback
        // only queues the row - touching the list from inside it costs the hook its place.
        Dispatcher.BeginInvoke(() => Add("hook", e.Down, e.Vk, detail, e.Injected ? Paints.Warn : Paints.Ink));
    }

    // ---------- what an application sees ----------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) => FromWindow(e, true);

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e) => FromWindow(e, false);

    private void FromWindow(KeyEventArgs e, bool down)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0 || !Interesting(vk)) return;

        string detail = Keyboard.Modifiers == ModifierKeys.None
            ? "no modifier held"
            : "held: " + Keyboard.Modifiers;
        if (down && e.IsRepeat) detail += ", repeat";

        Add("window", down, vk, detail, Paints.Dim);
    }

    private void Add(string source, bool down, uint vk, string detail, Brush paint)
    {
        var now = DateTime.Now;
        string delta = last == DateTime.MinValue ? "" : $"+{(now - last).TotalMilliseconds:F0} ms";
        last = now;

        var row = new LogRow(
            now.ToString("HH:mm:ss.fff"),
            delta,
            source,
            (down ? "down   " : "up     ") + Keys.Label(vk),
            detail,
            paint);

        rows.Add(row);
        while (rows.Count > 400) rows.RemoveAt(0);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Log.ScrollIntoView(row));
    }

    // ---------- controls ----------

    private void WatchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        watched = (int)vk;
        WatchBox.Text = Keys.Label(vk);
        e.Handled = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        rows.Clear();
        last = DateTime.MinValue;
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        timer.Stop();
        watch.Dispose();
        Win32.StopRawKeyboard();
    }
}

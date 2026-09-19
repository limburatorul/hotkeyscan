using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HotkeyScan;

internal enum Probe { Free, Taken, Error }

internal static class Win32
{
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr param);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr param);

    /// Asks the kernel for the hotkey. A refusal with ERROR_HOTKEY_ALREADY_REGISTERED is the
    /// only hard evidence in this tool: somebody else owns it. Success is released immediately.
    public static Probe TryClaim(Combo combo, out int error)
    {
        error = 0;
        const int id = 0x4855;
        if (RegisterHotKey(IntPtr.Zero, id, (uint)combo.Mods, combo.Vk))
        {
            UnregisterHotKey(IntPtr.Zero, id);
            return Probe.Free;
        }
        error = Marshal.GetLastWin32Error();
        return error == ErrorHotkeyAlreadyRegistered ? Probe.Taken : Probe.Error;
    }

    // ---------- watching what a keystroke does ----------

    public record WindowInfo(IntPtr Handle, uint Pid, string Process, string Title);

    public static WindowInfo? Foreground()
    {
        IntPtr h = GetForegroundWindow();
        return h == IntPtr.Zero ? null : Describe(h);
    }

    public static Dictionary<IntPtr, WindowInfo> VisibleWindows()
    {
        var found = new Dictionary<IntPtr, WindowInfo>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && Title(h).Length > 0) found[h] = Describe(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static WindowInfo Describe(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        string name;
        try { name = Process.GetProcessById((int)pid).ProcessName; }
        catch { name = "pid " + pid; }
        return new WindowInfo(h, pid, name, Title(h));
    }

    public static string Title(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ClassName(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static IEnumerable<IntPtr> AllWindows()
    {
        var all = new List<IntPtr>();
        EnumWindows((h, _) => { all.Add(h); return true; }, IntPtr.Zero);
        return all;
    }

    public static uint ClipboardSequence() => GetClipboardSequenceNumber();

    // ---------- pressing the combination ----------

    private const uint InputKeyboard = 1;
    private const uint KeyExtended = 0x0001;
    private const uint KeyUp = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public KEYBDINPUT Key;
        public int Pad1, Pad2;   // pads INPUT out to the size of its largest union member (MOUSEINPUT)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    /// Presses the combination for real. Whatever owns it will fire.
    public static void Press(Combo combo)
    {
        var down = new List<uint>();
        if ((combo.Mods & KeyMods.Ctrl) != 0) down.Add(0x11);
        if ((combo.Mods & KeyMods.Alt) != 0) down.Add(0x12);
        if ((combo.Mods & KeyMods.Shift) != 0) down.Add(0x10);
        if ((combo.Mods & KeyMods.Win) != 0) down.Add(0x5B);
        down.Add(combo.Vk);

        var events = new List<INPUT>();
        foreach (var vk in down) events.Add(Key(vk, false));
        for (int i = down.Count - 1; i >= 0; i--) events.Add(Key(down[i], true));

        var buffer = events.ToArray();
        SendInput((uint)buffer.Length, buffer, Marshal.SizeOf<INPUT>());
    }

    // ---------- watching the keyboard itself ----------

    public record KeyEvent(bool Down, uint Vk, uint Scan, bool Extended, bool Injected, bool LowIntegrity);

    private const int WhKeyboardLl = 13;
    private const uint LlkhfExtended = 0x01, LlkhfLowerIlInjected = 0x02, LlkhfInjected = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint Vk, Scan, Flags, Time;
        public IntPtr Extra;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc proc, IntPtr module, uint thread);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vk);

    // ---------- raw input: what the HID stack reports, under the hooks ----------

    public record RawKey(bool Down, uint Vk, uint Scan, bool E0, IntPtr Device);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort UsagePage, Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint Type, Size;
        public IntPtr Device, Param;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode, Flags, Reserved, VKey;
        public uint Message, Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUT
    {
        public RAWINPUTHEADER Header;
        public RAWKEYBOARD Keyboard;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr input, uint command, out RAWINPUT data, ref uint size, uint headerSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, StringBuilder? data, ref uint size);

    /// Raw input is delivered from the HID stack and a low-level hook cannot filter it, so a key
    /// that shows up here but never in the hook was swallowed by software - and one that shows up
    /// nowhere never left the keyboard.
    public static bool ListenToRawKeyboard(IntPtr window)
    {
        var keyboard = new RAWINPUTDEVICE
        {
            UsagePage = 0x01,
            Usage = 0x06,
            Flags = 0x00000100,   // RIDEV_INPUTSINK: keep reporting while another window has focus
            Target = window,
        };
        return RegisterRawInputDevices(new[] { keyboard }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    public static void StopRawKeyboard()
    {
        var remove = new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x06, Flags = 0x00000001, Target = IntPtr.Zero };
        RegisterRawInputDevices(new[] { remove }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    public static RawKey? ReadRawKey(IntPtr message)
    {
        uint size = (uint)Marshal.SizeOf<RAWINPUT>();
        if (GetRawInputData(message, 0x10000003, out var data, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) == uint.MaxValue)
            return null;
        if (data.Header.Type != 1) return null;   // RIM_TYPEKEYBOARD

        return new RawKey(
            Down: (data.Keyboard.Flags & 0x01) == 0,   // RI_KEY_BREAK means key up
            Vk: data.Keyboard.VKey,
            Scan: data.Keyboard.MakeCode,
            E0: (data.Keyboard.Flags & 0x02) != 0,
            Device: data.Header.Device);
    }

    /// Raw input reports the generic VK_CONTROL, never VK_LCONTROL, so the side has to be read
    /// off the scancode - which is the entire question here.
    public static uint SideOf(RawKey key) => key.Vk switch
    {
        0x11 => key.E0 ? 0xA3u : 0xA2u,
        0x10 => key.Scan == 0x36 ? 0xA1u : 0xA0u,
        0x12 => key.E0 ? 0xA5u : 0xA4u,
        _ => key.Vk,
    };

    public static string RawDeviceName(IntPtr device)
    {
        if (device == IntPtr.Zero) return "no device";
        uint size = 0;
        GetRawInputDeviceInfoW(device, 0x20000007, null, ref size);
        if (size == 0) return "unknown device";

        var text = new StringBuilder((int)size + 1);
        GetRawInputDeviceInfoW(device, 0x20000007, text, ref size);
        return text.ToString();
    }

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG message, IntPtr window, uint first, uint last);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint thread, uint message, IntPtr w, IntPtr l);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr W, L;
        public uint Time;
        public int X, Y;
    }

    /// A low-level keyboard hook on a thread of its own, with its own message loop. It has to be
    /// its own thread twice over: Windows silently drops a hook whose callback is slow to return,
    /// and registering raw input on the UI thread stops that thread's hook receiving anything.
    /// The hook only reads - every event is passed straight on.
    public sealed class KeyboardWatch : IDisposable
    {
        private const uint WmQuit = 0x0012;

        private readonly HookProc proc;   // a collected delegate would take the hook chain down
        private readonly Action<KeyEvent> report;
        private readonly Thread thread;
        private readonly ManualResetEventSlim ready = new();

        private IntPtr hook;
        private uint threadId;

        public KeyboardWatch(Action<KeyEvent> report)
        {
            this.report = report;
            proc = Callback;
            thread = new Thread(Pump) { IsBackground = true, Name = "keyboard watch" };
            thread.Start();
            ready.Wait(TimeSpan.FromSeconds(2));
        }

        public bool Installed => hook != IntPtr.Zero;

        private void Pump()
        {
            threadId = GetCurrentThreadId();
            hook = SetWindowsHookExW(WhKeyboardLl, proc, GetModuleHandleW(null), 0);
            ready.Set();
            if (hook == IntPtr.Zero) return;

            while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0) { }
            UnhookWindowsHookEx(hook);
        }

        private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int message = (int)wParam;
                report(new KeyEvent(
                    Down: message is 0x0100 or 0x0104,
                    Vk: data.Vk,
                    Scan: data.Scan,
                    Extended: (data.Flags & LlkhfExtended) != 0,
                    Injected: (data.Flags & LlkhfInjected) != 0,
                    LowIntegrity: (data.Flags & LlkhfLowerIlInjected) != 0));
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (threadId != 0) PostThreadMessageW(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            thread.Join(TimeSpan.FromSeconds(1));
        }
    }

    private static INPUT Key(uint vk, bool up) => new()
    {
        Type = InputKeyboard,
        Key = new KEYBDINPUT
        {
            Vk = (ushort)vk,
            Flags = (up ? KeyUp : 0) | (Keys.IsExtended(vk) ? KeyExtended : 0),
        },
    };
}

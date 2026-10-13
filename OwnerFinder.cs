using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace HotkeyScan;

/// Names the process behind a RegisterHotKey combination. For a few seconds a WH_GETMESSAGE
/// hook (native\hook.c) sits in every program; when the user presses the combination, Windows
/// posts WM_HOTKEY to the thread that registered it, the hook sees that thread take it out of
/// its queue and posts the process id back to a message-only window here.
/// 64-bit programs get hook64.dll from this process; 32-bit ones get hook32.dll from host32.exe.
internal static class OwnerFinder
{
    public record Hit(uint Pid, string Process, string Path, string Title);

    private const string SinkTitle = "HotkeyScan.OwnerSink";   // hook.c looks the window up by this
    private const int WmOwnerHit = 0x8000 + 0x48;               // WM_APP + 0x48, as in hook.c
    private const int WhGetMessage = 3;
    private const string TempPrefix = "HotkeyScan-hook-";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, IntPtr proc, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint message, uint action, IntPtr info);

    /// Waits up to `seconds` for the user to press `combo`. Null when nothing was caught.
    public static async Task<Hit?> Find(Combo combo, int seconds)
    {
        string dir = Extract();
        var caught = new TaskCompletionSource<uint>();
        Process? host = null;
        IntPtr dll = IntPtr.Zero, hook = IntPtr.Zero;

        var sink = new HwndSource(new HwndSourceParameters(SinkTitle) { ParentWindow = new IntPtr(-3) });   // HWND_MESSAGE
        try
        {
            // When HotkeyScan runs as admin, UIPI would drop the hit posted by an ordinary process.
            ChangeWindowMessageFilterEx(sink.Handle, WmOwnerHit, 1, IntPtr.Zero);   // MSGFLT_ALLOW
            int self = Environment.ProcessId;
            sink.AddHook((IntPtr _, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg != WmOwnerHit) return IntPtr.Zero;
                handled = true;
                uint pid = (uint)w.ToInt64();
                // WM_HOTKEY lParam: MOD_* flags in the low word, the virtual key in the high word.
                uint mods = (uint)l.ToInt64() & 0x0F, vk = ((uint)l.ToInt64() >> 16) & 0xFFFF;
                // A hook of the other bitness runs as a callback inside its installer, so those
                // report HotkeyScan's or host32's own pid - the real report comes from the matching DLL.
                if (pid != self && pid != (host?.Id ?? 0) && mods == (uint)combo.Mods && vk == combo.Vk)
                    caught.TrySetResult(pid);
                return IntPtr.Zero;
            });

            // Installed from the UI thread on purpose: 32-bit programs call this 64-bit hook back
            // through this thread's message loop, and WPF keeps it pumping.
            dll = LoadLibraryW(Path.Combine(dir, "hook64.dll"));
            if (dll == IntPtr.Zero) throw new InvalidOperationException($"could not load the hook (error {Marshal.GetLastWin32Error()})");
            hook = SetWindowsHookExW(WhGetMessage, GetProcAddress(dll, "GetMsgProc"), dll, 0);
            if (hook == IntPtr.Zero) throw new InvalidOperationException($"could not install the hook (error {Marshal.GetLastWin32Error()})");

            host = Process.Start(new ProcessStartInfo(Path.Combine(dir, "host32.exe"), seconds.ToString()) { UseShellExecute = false });

            var done = await Task.WhenAny(caught.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
            return done == caught.Task ? Describe(caught.Task.Result) : null;
        }
        finally
        {
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            if (host is { HasExited: false }) host.Kill();   // Windows removes its hook with its thread
            host?.WaitForExit(2000);
            sink.Dispose();
            if (dll != IntPtr.Zero) FreeLibrary(dll);
            Delete(dir);
        }
    }

    private static Hit Describe(uint pid)
    {
        string name, title = "";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
            title = p.MainWindowTitle;
        }
        catch (ArgumentException) { name = "pid " + pid; }   // it exited in the meantime
        return new Hit(pid, name, Win32.ExePath(pid), title);
    }

    /// The DLLs travel inside HotkeyScan.exe and only touch the disk while a search runs.
    private static string Extract()
    {
        foreach (var stale in Directory.GetDirectories(Path.GetTempPath(), TempPrefix + "*")) Delete(stale);

        string dir = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in new[] { "hook64.dll", "hook32.dll", "host32.exe" })
        {
            using var from = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException(name + " is missing from this build");
            using var to = File.Create(Path.Combine(dir, name));
            from.CopyTo(to);
        }
        return dir;
    }

    /// A program that has not touched its message queue since the unhook still has the DLL
    /// mapped, so the folder can refuse to go; the next search sweeps it up.
    private static void Delete(string dir)
    {
        try { Directory.Delete(dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

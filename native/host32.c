// Installs the 32-bit hook for 32-bit programs and pumps messages until HotkeyScan ends it,
// or for at most argv[1] seconds. A hook is removed by Windows when this thread exits.
#include <windows.h>
#include <stdlib.h>

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE prev, PWSTR cmd, int show)
{
    UNREFERENCED_PARAMETER(instance); UNREFERENCED_PARAMETER(prev); UNREFERENCED_PARAMETER(show);
    wchar_t path[MAX_PATH];
    DWORD n = GetModuleFileNameW(NULL, path, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return 1;
    wchar_t *slash = wcsrchr(path, L'\\');
    if (!slash || (slash - path) + 12 >= MAX_PATH) return 1;
    wcscpy_s(slash + 1, MAX_PATH - (slash + 1 - path), L"hook32.dll");

    HMODULE dll = LoadLibraryW(path);
    if (!dll) return 2;
    HOOKPROC proc = (HOOKPROC)GetProcAddress(dll, "GetMsgProc");
    if (!proc) return 3;
    HHOOK hook = SetWindowsHookExW(WH_GETMESSAGE, proc, dll, 0);
    if (!hook) return 4;

    int seconds = _wtoi(cmd);
    if (seconds <= 0 || seconds > 60) seconds = 10;
    SetTimer(NULL, 0, seconds * 1000, NULL);

    MSG msg;
    while (GetMessageW(&msg, NULL, 0, 0) > 0)
    {
        if (msg.message == WM_TIMER) break;
        DispatchMessageW(&msg);
    }
    UnhookWindowsHookEx(hook);
    return 0;
}

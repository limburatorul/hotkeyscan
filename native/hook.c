// WH_GETMESSAGE hook: when the thread that registered a hotkey takes its WM_HOTKEY out of the
// queue, tell HotkeyScan which process that was. Built twice, x64 and x86, from this one file.
#include <windows.h>

#define SINK_TITLE L"HotkeyScan.OwnerSink"
#define WM_OWNER_HIT (WM_APP + 0x48)

// Exported undecorated on both: x86 would otherwise name the __stdcall export _GetMsgProc@12.
#ifdef _M_IX86
#pragma comment(linker, "/EXPORT:GetMsgProc=_GetMsgProc@12")
#else
#pragma comment(linker, "/EXPORT:GetMsgProc")
#endif

LRESULT CALLBACK GetMsgProc(int code, WPARAM wParam, LPARAM lParam)
{
    if (code == HC_ACTION && wParam == PM_REMOVE)
    {
        const MSG *msg = (const MSG *)lParam;
        if (msg->message == WM_HOTKEY)
        {
            // The sink is a message-only window, so only FindWindowEx under HWND_MESSAGE finds it.
            HWND sink = FindWindowExW(HWND_MESSAGE, NULL, NULL, SINK_TITLE);
            if (sink)
                PostMessageW(sink, WM_OWNER_HIT, (WPARAM)GetCurrentProcessId(), msg->lParam);
        }
    }
    return CallNextHookEx(NULL, code, wParam, lParam);
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    UNREFERENCED_PARAMETER(reserved);
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(instance);
    return TRUE;
}

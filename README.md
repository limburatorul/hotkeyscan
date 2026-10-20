# HotkeyScan

Find out which program is holding a key combination on Windows.

`HotkeyScan.exe` is one self-contained file: it runs on any 64-bit Windows PC with no .NET
installed. Double-click and it starts.

## How to use it

- **One combination** — click the *Hotkey* box and press the combination (or type it, for one
  Windows swallows before we see it, like `Win+L`), then **Check**.
- **All at once** — **Scan them all** sweeps ~760 common combinations. Tick *only unknown owners*
  to keep just the real conflicts: the ones somebody holds but Windows won't say who.
- **Who reserved it** — for a combination the probe says is taken, **Find the owner** names the
  program that registered it, whatever it is: process name, exe path and window title. See below.
- **Who answers** — double-click a row to load it at the top, then **Press it** actually presses
  the combination (or a `double-tap Win`/`Ctrl`/`Alt`/`Shift`) and, within about 1.5 s, reports
  the process whose window came to the front, appeared, or took the keyboard focus, with its exe
  path. If nothing reacts it says so: *held by a keyboard hook or nothing; owner unknown* — not
  "free".
- **Not recognised? Tell us** — next to an unknown owner. It shows the report first (the
  combination, HotkeyScan version, Windows build and the names of running processes); delete any
  line, then it opens your mail app addressed to feedback@protagonistlabs.app. Nothing is sent by
  HotkeyScan, and it never connects to the internet.
- **Our own apps** — Lume, Frostpane and Auto Clicker list their hotkeys on their Protagonist Labs
  cards (`%APPDATA%\ProtagonistLabs\apps`). When two of them want the same combination, Check, the
  scan list and the status line say so once: *Lume and Frostpane both use Alt+Space — change it in
  one of them*. A card that says its app holds a hotkey is compared with the live probe, so one left
  behind by a crash reads *nothing holds it now*.

## Find the owner — how it works

`RegisterHotKey` doesn't say who holds a combination, but when you press it Windows posts
`WM_HOTKEY` to the thread that registered it. **Find the owner** installs a `WH_GETMESSAGE` hook
for 10 seconds and asks you to press the combination; the hook sees which process takes that
message out of its queue, tells HotkeyScan, and is removed at once (on the first hit or the
timeout).

- It is opt-in and asks first: the hook means a small DLL is briefly loaded into running
  programs. **Games with anti-cheat may object — close games first.**
- 64-bit and 32-bit programs are both covered (`hook64.dll`, and `hook32.dll` installed by a tiny
  `host32.exe`). They travel inside `HotkeyScan.exe` and are written to a temp folder only while a
  search runs, then deleted (a folder still in use is swept on the next search). Unsigned, like
  the exe.
- Programs running as administrator are reachable only when HotkeyScan runs as administrator too;
  when nothing is caught, the result says so.
- It catches `RegisterHotKey` owners only. Keyboard-hook apps (Raycast, Discord, Steam's overlay)
  get no `WM_HOTKEY` — use **Press it** for those.

## Key monitor — one key that misbehaves

**Key monitor** answers a different question from the rest of the app: not "who holds this
combination", but "does this key reach applications at all?". It's what you want when a key gives
out on its own, or when the left side of a modifier works and the right one doesn't — an asymmetry
`RegisterHotKey` can't cause, because it doesn't tell left from right.

The window puts three sources side by side:

- **raw** — what the keyboard reported to Windows through the HID stack, **below every hook**.
  This layer settles "is it the keyboard or a program": press and no `raw` row appears, and the key
  never left the keyboard. It shows the scancode and **which device** it came from — a row that
  says `no device (synthesized)` was made by software, not pressed.
- **window** — what an application actually receives, with the modifiers active at that moment. A
  `raw` row with no `window` row after it means something ate the key on the way.
- **hook** — the hook chain, as a midway reference.

Plus **held down right now**: the real state of every modifier, left and right separately, so a
stuck key is visible.

> **About the `hook` layer:** while a process asks for raw input, its keyboard hook stops receiving
> anything — seen on a PC with a Kaspersky keyboard filter driver (`klkbdflt`) loaded, and still
> with Kaspersky closed. The list header says `HOOK: BLOCKED IN THIS PROCESS` when that happens, so
> the missing rows don't look like a bug. Nothing is lost: `raw` also catches software-made events
> and marks them `no device`.

Plus the **what could rewrite a key** panel: the Scancode Map in the registry (driver-level
remapping, below every hook, and left/right aware), PowerToys Keyboard Manager remaps (including
shortcut remaps), the **keyboard filter drivers** registered in the registry (anything besides
`kbdclass` is third-party and sits below all hooks), and the Sticky Keys / Filter Keys state.

How to read it:

| What you see | What it means |
|---|---|
| no `raw` row on a press | the key never left the keyboard: the physical key, its firmware, or the driver |
| `raw` but no `window` after it | some software swallowed it on the way |
| `raw` saying `no device` | the event was made by a program, not pressed |
| `raw` on press, `window` only on the next key | something holds the modifier and replays it later |
| a chip that stays lit | a modifier stuck down — on its own it breaks every shortcut |

Only the modifiers and the key you pick in the box are logged — not everything you type.

## The four layers, in order of trust

1. **The kernel probe** — tries `RegisterHotKey`; a refusal with `ERROR_HOTKEY_ALREADY_REGISTERED`
   is the only certain proof that someone else owns the combination. Windows doesn't say who.
2. **The Windows table** — the shortcuts the system reserves, so they don't show up as
   "owner unknown" next to the real culprits.
3. **Application configs** — reads the hotkeys declared by PowerToys, ShareX, OBS and running
   AutoHotkey scripts, plus Raycast's log and the cards of our own apps.
4. **Find the owner** — catches the `WM_HOTKEY` Windows posts to whoever registered the combination,
   so it names any `RegisterHotKey` owner, listed above or not.
5. **The empirical test** — actually presses the combination and watches which window, focus,
   process or clipboard moves. It asks first, because it really triggers the hotkey's action.

## The hard limit

Apps that catch keys through a global hook (Raycast, Discord, Steam's overlay, peripheral software,
and AutoHotkey for some keys) reserve nothing in the kernel, so neither the probe nor **Find the
owner** can see them — only the empirical test catches them, and only if they do something
visible. Windows has no API that says which hook ate a key. What goes through a keyboard driver
can't be seen from user mode at all. Keys pressed by the empirical test don't reach a window that
runs as administrator unless HotkeyScan does too.

## From source

Needs the .NET 8 SDK and the MSVC build tools (x64 and x86); the csproj runs
`native\build.cmd` to build the hook before compiling.

```bash
dotnet run
dotnet publish -c Release -o dist
```

| File | What it is |
|---|---|
| `MainWindow.xaml` / `.cs` | the window and the orchestration |
| `Win32.cs` | the `RegisterHotKey` probe, `SendInput`, window watching |
| `Combo.cs` | parsing combinations, and the swept set |
| `Reserved.cs` | the table of Windows shortcuts |
| `KeyMonitor.xaml` / `.cs` | the key monitor |
| `OwnerFinder.cs` | Find the owner: extracts the hook, installs it, waits for the hit |
| `native\hook.c`, `native\host32.c` | the `WH_GETMESSAGE` hook DLL and the 32-bit host |
| `Remaps.cs` | Scancode Map, PowerToys Keyboard Manager, Sticky/Filter Keys |
| `AppScan.cs` | PowerToys / ShareX / OBS / AutoHotkey configs, our apps' cards, known apps |
| `Ecosystem.cs` | the Protagonist Labs card: writing ours, reading the others (untrusted) |
| `tests\` | card-reader tests on synthetic folders: `dotnet run --project tests` |

## Licence

MIT. Made by [Protagonist Labs](https://protagonistlabs.app/hotkeyscan/).

## More from Protagonist Labs

- [Lume](https://protagonistlabs.app/lume/?utm_source=github&utm_medium=readme&utm_campaign=hotkeyscan): a free keyboard launcher on Alt+Space.
- [Auto Clicker](https://protagonistlabs.app/autoclicker/?utm_source=github&utm_medium=readme&utm_campaign=hotkeyscan): a free auto clicker with routes and image waits.
- [All apps](https://protagonistlabs.app/?utm_source=github&utm_medium=readme&utm_campaign=hotkeyscan): Windows apps that each do one job properly.

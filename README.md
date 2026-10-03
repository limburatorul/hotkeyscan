# HotkeyScan

Find out which program is holding a key combination on Windows.

`HotkeyScan.exe` is one self-contained file: it runs on any 64-bit Windows PC with no .NET
installed. Double-click and it starts.

## How to use it

- **One combination** — click the *Hotkey* box and press the combination (or type it, for one
  Windows swallows before we see it, like `Win+L`), then **Check**.
- **All at once** — **Scan them all** sweeps ~760 common combinations. Tick *only unknown owners*
  to keep just the real conflicts: the ones somebody holds but Windows won't say who.
- **Who answers** — double-click a row to load it at the top, then **Press it** actually presses
  the combination and tells you which process reacted.

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
   AutoHotkey scripts. The only source that gives exact attribution.
4. **The empirical test** — actually presses the combination and watches which window, process or
   clipboard moves. It asks first, because it really triggers the hotkey's action.

## The hard limit

Apps that catch keys through a global hook (Discord, Steam's overlay, peripheral software, and AutoHotkey for some keys) reserve
nothing in the kernel, so the first layer can't see them — only the empirical test catches them,
and only if they do something visible. What goes through a keyboard driver can't be seen from user
mode at all.

## From source

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
| `Remaps.cs` | Scancode Map, PowerToys Keyboard Manager, Sticky/Filter Keys |
| `AppScan.cs` | PowerToys / ShareX / OBS / AutoHotkey configs and known apps |

## Licence

MIT. Made by [Protagonist Labs](https://protagonistlabs.app/hotkeyscan/).

## More from Protagonist Labs

- [Lume](https://protagonistlabs.app/lume/?utm_source=github&utm_medium=readme&utm_campaign=hotkeyscan): a free keyboard launcher on Alt+Space.
- [Auto Clicker](https://protagonistlabs.app/autoclicker/?utm_source=github&utm_medium=readme&utm_campaign=hotkeyscan): a free auto clicker with routes and image waits.
- [All apps](https://protagonistlabs.app/?utm_source=github&utm_medium=readme&utm_campaign=hotkeyscan): Windows apps that each do one job properly.

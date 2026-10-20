# HotkeyScan 1.1.0 — our own apps' hotkeys, and their clashes

## What

- Reads the hotkeys Lume, Frostpane and Auto Clicker declare on their Protagonist Labs cards
  (`%APPDATA%\ProtagonistLabs\apps\<id>.json`, schema 1, `hotkeys: [{combo, does, registered}]`) as
  `Declared(card name, does, combo, "Protagonist Labs card")`, alongside PowerToys, ShareX, OBS,
  AutoHotkey and Raycast (`AppScan.Studio`).
- Two or more of our apps on one combination give one line, in Check (verdict "our apps"), in the
  scan list (one row) and in the status line at start:
  *Frostpane and Lume both use Alt+Space — change it in one of them*;
  three: *Auto Clicker, Frostpane and Lume all use Alt+Space — change it in all but one of them*.
  Names come from the cards (`AppScan.Clashes`).
- `registered` is read together with the live probe: `true` while nothing holds the combination
  reads *its card says it holds it, but nothing does now: it isn't running, or it stopped without
  updating its card* (a crash leaves `true` behind); `false` reads *it could not register it:
  something else holds it* or *…nothing holds it now, so restarting it should*.
- Scan them all now also probes declared combinations outside the ~760 swept ones (Alt+Space, a
  bare F6). Before, any declared combination outside the sweep was labelled
  "keyboard hook, nothing reserved" without being probed.
- Works with: "Lume · sends a hotkey it can't register here, and its own are checked against our other apps".

## Why

Plan item 8 (ecosystem integrations, 20.10): only these three apps register global hotkeys, and
Lume (Alt+Space) and Frostpane could be set to the same one; whichever starts second silently
loses it. HotkeyScan already reads declarations, so the cards are one more source.

## Untrusted input

Every card is another app's file. One shared reader (`Ecosystem.Load`) now serves both the Works
with line and the hotkeys:
card ≤ 64 KB (checked before reading), BOM tolerated, root must be an object, `schema` must be the
number 1, `name` a string of 1–64 chars, `exe` a fully qualified path on a drive of this PC that
exists. UNC and device paths (`\\host\share`, `//host/share`, `\\.\`, `\\?\`, `//?/`) are refused
before `File.Exists` — the same rule Frostpane adopted after a `//host/share` exe hung its check
21 s. Per card at most 16 hotkey entries (more: the card is skipped); per entry `combo` a string ≤ 64
chars that `Keys.TryParse` accepts (modifier order and case don't matter, never compared as text),
`does` a string of 1–200 chars, `registered` a bool or else unknown. Anything else is skipped,
never thrown. `ReadCard` (Works with) used to call `File.Exists` on any exe string; it now goes
through the same rule.

## How it was tested

`tests\` — a console project linking `AppScan.cs`, `Combo.cs`, `Ecosystem.cs`, `Win32.cs`,
`Updater.cs` (the Backup Labs pattern; no packages). It writes synthetic `apps\` folders in a
scratch directory and calls `AppScan.Studio(folder)`; it never reads the real
`%APPDATA%\ProtagonistLabs`, registers a hotkey or opens a window. `dotnet run -c Release --project tests`:

```
ok   Space+Alt and alt+space are read as the same Alt+Space
ok   two apps on Alt+Space give one line: Frostpane and Lume both use Alt+Space — change it in one of them
ok   registered is carried through as declared
ok   three apps give one line: Auto Clicker, Frostpane and Lume all use Alt+Space — change it in all but one of them
ok   Alt+Space, Win+Alt+Space and F6: three hotkeys, no clash
ok   a bare F6 is read
ok   one app twice on a combo is no clash; no registered field is null
ok   hostile folder: only the BOM card and the one good entry survive (Bom:Ctrl+Shift+B, Entries:Ctrl+Alt+9)
ok   registered: "yes" is not a bool, so it is unknown
ok   UNC and device exes refused without touching the network: 4 ms for the whole folder
ok   a missing apps folder is no hotkeys
all passed
```

The hostile folder holds: schema 2, UNC exe, `//host/share` exe, three device-path spellings of
an exe that does exist, a relative exe, a missing exe, 8 MB of junk, 20 entries, a JSON array,
`schema: "1"` as a string, an entry list with a number combo, an unknown key, a 201-char `does`,
an empty `does`, a bare string, a null, a non-bool `registered`, 10 000 nested brackets, a `.txt`
card, and a valid card saved with a UTF-8 BOM (read). With the local-path rule removed the test
fails (the `\\.\` and `//?/` cards get through), so it guards the rule.

`dotnet build -c Release` and `dotnet publish -c Release` (to a scratch folder): 0 warnings,
0 errors; the single-file exe reports 1.1.0, 66.1 MB.

## Not tested

- The window itself: Check / Scan / status line with real cards were not run, because the user's
  HotkeyScan may be running and the app reads the real `%APPDATA%` (the known-folder API ignores a
  redirected `APPDATA`), and Check registers combinations briefly. The UI paths only format what
  `Studio`/`Clashes` return, plus the `Held` switch over (registered, probe).
- Cards written by the real Lume 0.3.0, Frostpane 1.19.0 and Auto Clicker 1.0.12 (their formats
  were read from source: Lume `toHotkeyScanCombo`, Frostpane `PeekHotkeyName`, Auto Clicker
  `hotkeyscan_combo`).

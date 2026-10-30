# Dynamic Island for Windows — project reference

The full project reference. Read it before changing the app, and **update it in the same change**
whenever behaviour, structure, constants, or the build/install process changes.

## What it is

A WPF (.NET 8, C#) desktop app styled after Apple's Dynamic Island:

- **Hidden:** a 40×5 DIP glossy black tab against the top edge of the screen, centered by default.
- **Hover** over the activation area → a droplet animation (the tab stretches down, pinches off the
  edge with a liquid neck, then widens into a floating **compact pill**) that shows the current activity.
- **Click** the pill → it opens into the **expanded panel** (440×336): music, timer, audio output, and file shelf.
- **Auto-hide:** the compact pill hides once the pointer leaves the safe zone for `HideDelayMs`. The
  expanded panel ignores pointer distance and closes only on a click outside it (or Esc collapses it to the pill).
- A **timer finishing** opens a shaking alert that rings for 15 s and stays until it's clicked.
- Two activities at once → the pill shows the first, and a round **bubble** splits off its right side for the second.

The user wants an iOS look and motion. They dislike wobble or shaking on open/close (only the timer alert
shakes, on purpose), so keep springs critically damped or only gently underdamped (≥0.8).

## Build, install, run

No Node or Python on this PC. The .NET 8 and 9 SDKs are installed, and so are Git and the GitHub CLI, though
they're not on the default shell path: use `C:\Program Files\Git\cmd\git.exe` and `C:\Program Files\GitHub CLI\gh.exe`.

**Repo:** GitHub `dynamic-island-for-windows` (public). The repo root is `proj_1/`, holding `README.md`,
`docs/screenshots/`, `CLAUDE.md` and `DynamicIsland/`. `.gitignore` excludes bin/obj and the local
`.agents/`, `.claude/` and `skills-lock.json`. Keep `README.md` in step with user-visible changes, and
re-render the screenshots when the visuals change:

```powershell
dotnet build -nologo -v q
Start-Process .\bin\Debug\net8.0-windows10.0.19041.0\DynamicIsland.exe -ArgumentList "--screenshots","..\docs\screenshots" -Wait
```

```powershell
# Build (from DynamicIsland/)
dotnet build -nologo -v q

# Install = publish into the user's programs folder (stop the running copy first)
Get-Process DynamicIsland -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish DynamicIsland.csproj -c Release -o "$env:LOCALAPPDATA\Programs\DynamicIsland" -nologo -v q
Start-Process "$env:LOCALAPPDATA\Programs\DynamicIsland\DynamicIsland.exe"
```

- After every code change: rebuild, reinstall (publish), and relaunch.
- Start Menu shortcut: `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Dynamic Island.lnk`.
- Start with Windows: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `DynamicIsland` = quoted exe path. It's on, and Settings has a toggle for it.
- Single instance: the mutex `DynamicIsland.SingleInstance`.
- User settings: `%APPDATA%\DynamicIsland\settings.json`.
- The `WFAC010` warning is suppressed on purpose: WPF takes its DPI mode from the manifest.

## Files

| File | Role |
|---|---|
| `DynamicIsland.csproj` | `net8.0-windows10.0.19041.0`, WPF + WinForms (WinForms only for the tray `NotifyIcon`). The `System.Windows.Forms`/`System.Drawing` implicit usings are removed to avoid name clashes, so WinForms types are fully qualified. Copies `BrowserExtension\**` to the output. |
| `app.manifest` | PerMonitorV2 DPI awareness. |
| `App.xaml(.cs)` | Startup: single-instance mutex, loads `Settings`, creates `MainWindow` + `TrayIcon`. `OpenSettings()`, `Quit()`. `ShutdownMode=OnExplicitShutdown`. |
| `MainWindow.xaml(.cs)` | The island: state machine, springs, geometry, hover/auto-hide, every widget's UI and logic. The bulk of the app. |
| `MainWindow.Screenshots.cs` | `DynamicIsland.exe --screenshots <dir>` (bypasses the mutex, stays off-screen) poses each state with demo data and saves @2x PNGs. Used for `../docs/screenshots/`. Re-run it after visual changes. |
| `Spring.cs` | Damped spring with Apple-style params (`DampingRatio`, `Response` s); `Snap`, `SetVelocity`, `IsSettled`. Fixed 1/240 s substeps. |
| `MediaService.cs` | Windows' system media controls (`GlobalSystemMediaTransportControlsSessionManager`). Raises `MediaState?` (title, artist, thumbnail bytes, playing, can-prev/next/playpause, `SourceApp` AUMID) on a background thread. Prefers a playing session. |
| `AudioOutputs.cs` | Lists playback devices (MMDevice COM) and switches the default (**undocumented `IPolicyConfig`**, as EarTrumpet uses) for all three roles. |
| `IslandTimer.cs` | Countdown computed from the wall clock (no drift); `Changed`, `Finished`; `Format()`. |
| `WheelPicker.cs` | iOS-style looping number wheel (custom `FrameworkElement`, drawn in `OnRender`). Wheel, drag with momentum projection, click a row. Used for the timer's min/sec. |
| `SettingsWindow.cs` | Built in code (no XAML). Every control applies and saves immediately. Also holds the start-with-Windows toggle and the browser-extension setup steps. |
| `Settings.cs` | JSON settings: `MonitorIndex` (-1 = primary), `OffsetX` (0), `ActivationWidth` (90), `ActivationHeight` (8), `SafeMargin` (60), `HideDelayMs` (500), `RevealMs` (400), `HideInFullscreen` (true). |
| `TrayIcon.cs` | WinForms `NotifyIcon` (a white pill drawn in code): Settings…, Quit. |
| `NativeMethods.cs` | P/Invoke: cursor pos, async key state, foreground/fullscreen checks, monitor DPI, `SetWindowPos`, tool-window style, `keybd_event`. |
| `BrowserExtension/` | MV3 Chrome/Edge extension. A global command `Ctrl+Shift+9` toggles the browser's native PiP on the audible tab's largest video. |

## MainWindow architecture

**Window:** 560×400 DIP, transparent, topmost, no taskbar button, and the `WS_EX_TOOLWINDOW` style
(hidden from Alt+Tab), `ShowActivated=False`. Positioned in physical pixels with `SetWindowPos` at the
top of the chosen display, plus `OffsetX`. It repositions on DPI change and on display-settings change.
Transparent pixels pass clicks through to whatever is underneath.

**States:** `Hidden → Compact → Expanded`, plus `Alert` (timer done). Transitions:

- `Reveal()` (droplet; staged with `Task.Delay` + a `_sequence` counter that cancels stale stages)
- `Retract()` (the same path in reverse)
- `Expand()`, `Collapse()`
- `ShowAlert()`, `DismissAlert()`

**Shape:** the springs `_top` (distance from the edge), `_width`, `_height`, `_scale` (press feedback),
`_shake` (alert), and `_bubble` (0–1 split) are stepped on `CompositionTarget.Rendering` and only run
while unsettled. Each frame, `ApplyFrame()` rebuilds the geometry:

- a rounded rect whose top corners stay flat while attached to the edge;
- a liquid neck (two quadratic curves) while `_top` < `FloatGap`;
- the bubble circle, unioned in.

The result is assigned to `Body` (fill + shadow) and `Gloss` (sheen + edge stroke), and `ContentLayer.Clip`
is set to the body. Content opacity and scale come from the shape's progress, so interrupting an
animation never desyncs the content.

**Key constants (DIP):**

| Constant | Value |
|---|---|
| Tab | 40×5 |
| `DropWidth` | 28 |
| `FloatGap` | 10 |
| `CompactHeight` | 34 |
| Compact widths | media 170 / timer 150 / shelf 136 / idle 110 |
| Expanded | 440×336, radius 30 |
| Alert | 340×84 |
| `BubbleGap` | 8 |

**Hover detection:** a 16 ms timer polls `GetCursorPos`, so there are no invisible hit areas.

- **Hidden:** the activation rect (width × height at the top edge) triggers `Reveal()`.
- **Compact:** a safe zone (the target rect inflated by `SafeMargin`, plus a corridor up to the edge). Outside it for `HideDelayMs` → `Retract()`, unless `KeepOpen()` (dragging, a menu open, a recent drag-over, mouse captured, typing).
- **Expanded:** `TrackOutsideClick()` watches L/R button state. A press that starts and ends outside the panel closes it, unless a drag-over happened during the press (e.g. dragging a file in from Explorer).
- **Alert:** ignores the pointer; only a click on the island dismisses it.

**Fullscreen:** a check every 1 s (`SHQueryUserNotificationState` D3D/presentation, or a non-maximized
foreground window covering the display) hides the island. It's skipped while the alert is showing.

**Activities:** `Activities()` lists, in order: timer (running or done), playing media, shelf files,
then paused media (only if nothing else). The first goes in the pill (`PrimaryActivity`), the second in
the bubble (`SecondaryActivity`).

## Widgets

- **Music:** artwork, title, artist, prev/play-pause/next. The play icon flips immediately when clicked. The compact pill shows small artwork and animated bars.
- **Picture-in-picture:** hovering the expanded artwork shows a PiP icon. Clicking it (only for a browser source: chrome/msedge/brave/opera/vivaldi) sends **Ctrl+Shift+9** via `keybd_event`, then calls `Retract()`. The extension then puts the real video into the browser's own PiP. It needs the extension loaded unpacked once (Settings has the steps) and its shortcut set to Global. Windows can't hand another app's video stream over, and a window-mirror approach was tried and removed at the user's request. Some sites block PiP. **Not yet confirmed:** whether the browser allows PiP for a background tab.
- **Timer:** min/sec `WheelPicker`s while setting; a large countdown while running or paused; Start/Pause/Resume and Reset. When finished: `ShowAlert()` opens the alert ("Timer ended", "MM:SS timer · click to dismiss") through the droplet if hidden. It shakes every 2.5 s while ringing, and `C:\Windows\Media\Alarm01.wav` loops through `MediaPlayer` for 15 s. The alert stays until it's clicked; clicking stops the sound, resets the timer, and retracts the island.
- **Audio output:** the device list is refreshed each time the panel opens. Clicking a device sets it as the default.
- **File shelf:** drop files or folders on the pill or panel (dragging onto the pill expands it). The shelf keeps paths, not copies, and empties on quit. Drag a tile out to any app; the remove (×) button appears on hover. Horizontal scroll uses the mouse wheel.

## Design rules in use (from the project's `.claude/skills`, mainly apple-design / animate)

- Springs everywhere for shape motion, interruptible from the current value. Default damping 1.0; the reveal and expand use 0.8–0.85 (very gentle).
- Press feedback on mouse-down, commit on mouse-up. Buttons scale to 0.88 with `KeySpline 0.23,1 0.32,1`.
- No `ease-in`. Fades are 120–200 ms with a strong ease-out.
- Respect `SystemParameters.ClientAreaAnimation` (reduce motion): no bounce, no bars, no shake.
- The skills are written for the web; translate their rules to WPF.

## Change log

- Step 1: a basic pill plus now playing. Later: an iOS 26 gradient look, then a full redesign to the droplet/tab spec.
- Centered by default, the expanded panel closes only on an outside click, and the timer slider was replaced by wheels.
- Timer alert (shake, 15 s alarm, click to dismiss) and the secondary-activity bubble.
- PiP: the live window mirror (removed) was replaced by the browser extension plus a global hotkey.
- Auto-launch on Windows sign-in turned on.
- GitHub repo, README and the rendered-screenshot mode. Fixed the misaligned default device in the output list (the check icon now always reserves its width).

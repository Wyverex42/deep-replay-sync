# ReplaySync.App

The desktop app (WPF) for the replay sync. The sync itself is `SyncController` in [../ReplaySync](../ReplaySync),
shared with the console.

```bash
cd src/ReplaySync.App
dotnet run -c Release
```

Needs Windows 10 2004 or later. On Windows 10 the two chart windows get a yellow border while their clocks are read
(Windows draws it around every captured window; only Windows 11 lets it be turned off).

## Use

- **On launch nothing is pressed.**
- **Play** plays Deepdom and syncs: Deepchart starts by itself as soon as it is safely behind, and is paused and
  resumed from then on. While syncing a Deepchart paused by hand follows again.
- **Pause** pauses both and ends the sync. To run just one app, pause here and use that app's own replay window.
- **Speed**: x1, x8, x40, x100 or a typed 1 to 100 sets both apps. Above x1 Deepchart is kept roughly in sync and
  may be a little ahead ("Fast forward"). x1 is for trading: Deepchart is held while Deepdom plays on fast until it
  has passed Deepchart ("Slowing down to x1"), then both go to x1.
- **Closing the window** pauses both, also without the sync. It waits up to 30 s for an app which does not answer,
  then names what it could not pause.
- **Clean start** resets both replays to a minute and plays them in sync, after a confirmation. Deepdom starts,
  Deepchart follows a few seconds later, once Deepdom is seen past that minute. While an app does
  not respond it keeps waiting and says so, until it answers, exits or you press Cancel.
- **Settings** are margin, hysteresis and catch-up. Saved in `%LOCALAPPDATA%\DeepReplaySync\settings.json`.

## For a small gap

The app reads the seconds from the two chart windows:

- **Deepdom**: the time axis at the bottom of the chart. Keep it visible, not scrolled back, not zoomed far out.
- **Deepchart**: the bar countdown at the bottom right. Use bars of one minute or more.

The windows can be covered but not minimized. Without the seconds Deepchart is held until each minute change: safe,
but up to a minute or so behind. Each app's line says "reading seconds" or "minute clock only".

## The banner

| Colour | Meaning |
|---|---|
| Grey | Paused |
| Green | Playing in sync, including the short pauses. Or fast forward |
| Amber | Deepchart is held or reset, the text says why. Or Deepdom plays without the sync |
| Red | Not guarded or ahead: Deepchart plays by itself, not responding, not found, not updating |

The large number is the best guess of the gap. "Worst case" below it is what the sync decides on.

## Files

| File | What |
|---|---|
| `SyncRunner.cs` | Runs the controller on its own thread, takes commands, publishes snapshots. |
| `Banner.cs` | Which state is shown, from a snapshot. |
| `MainWindow.xaml(.cs)` | The window. Never calls UI Automation itself. |
| `Settings.cs` | The saved settings and their valid ranges. |
| `Theme.xaml` | Colours and control styles. |
| `app.ico` | The icon of the exe, the window and the taskbar. |

Only one instance of the app can run. It does not detect the console: don't sync with both at once.

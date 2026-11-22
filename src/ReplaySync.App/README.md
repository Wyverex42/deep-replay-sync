# ReplaySync.App

The desktop app (WPF) for the replay sync. The sync itself is `SyncController` in [../ReplaySync](../ReplaySync),
shared with the console.

```bash
cd src/ReplaySync.App
dotnet run
```

## Use

- **On launch nothing is pressed.** The sync is off and the window only measures.
- **Start sync** lets the tool pause and resume Deepchart. Deepdom is not touched.
- **Let Deepchart follow** is for a Deepchart which was paused by hand: the sync resumes it once it is safely behind.
- **Stop sync** and **closing the window** leave Deepchart paused when it was syncing. (The console resumes it.)
- **Clean start** is the only thing which touches Deepdom: after a confirmation it resets both replays to a minute,
  plays them and turns the sync on. It can be cancelled while loading. A failed or cancelled one leaves the sync off.
- **Settings** are margin, hysteresis, Deepdom safety and catch-up (see [the options](../ReplaySync/README.md#options)).
  They are saved in `%LOCALAPPDATA%\DeepReplaySync\settings.json`.

There are deliberately no Play / Pause buttons for Deepdom and no keyboard shortcuts.

## The banner

| Colour | Meaning |
|---|---|
| Grey | Sync off, measuring only |
| Green | Following, including the short pauses |
| Amber | Deepchart is held or paused, the text says why |
| Red | Not guarded or ahead: sync off while Deepchart plays, not responding, not found, not updating |

The large number is the best guess of the gap. "Worst case" below it is what the sync decides on.

## Files

| File | What |
|---|---|
| `SyncRunner.cs` | Runs the controller on its own thread, takes commands, publishes snapshots. |
| `Banner.cs` | Which state is shown, from a snapshot. |
| `MainWindow.xaml(.cs)` | The window. Never calls UI Automation itself. |
| `Settings.cs` | The saved settings and their valid ranges. |
| `Theme.xaml` | Colours and control styles. |

Only one instance of the app can run. It does not detect the console: don't sync with both at once.

# ReplaySync

Keeps the Deepchart replay from getting ahead of the Deepdom replay. Background, measurements and design decisions are
in [docs/findings.md](../../docs/findings.md).

## Rules

- **Deepdom is the reference.** Syncing only pauses and resumes Deepchart. Deepdom is only pressed by Play, Pause
  (the app's button, the console's `P` / `Space`), the speed buttons (console `1` to `4`), a clean start and
  closing the app.
- **Above x1 a little ahead is accepted.** Both faster than x1 is fast forward, never traded: Deepchart is kept
  within about one wall second of playback of Deepdom. Speed changes press Deepdom first going up and Deepchart first
  going down. Slowing to x1 holds Deepchart and lets Deepdom pass it at the old speed before both are slowed.
  The fastest speed offered is x100 (x200 froze Deepdom).
- **A Deepdom chart which does not move holds Deepchart**: its box changed minute twice while the axis label stood
  still (seen at 14:11:56 on 06/01/2026, or a chart scrolled back).
- **At x1 Deepchart must never be ahead.** Deepchart's replay time is taken as an upper bound, Deepdom's as a lower bound.
  Deepchart is paused when its upper bound is less than `--margin` seconds behind Deepdom's lower bound, and resumed
  when it is `--margin + --hysteresis` behind.
- **Deepdom's lower bound is never extrapolated.** It moves only with what was seen: a label on its time axis, or a
  minute change. No assumed rate is safe.
- **A changed Deepdom time counts only after its next rollover while playing.** Until then Deepchart is held, as text
  typed into the date box looks exactly like a new replay time. A clean start needs no such wait, also one which did not finish: once Deepdom is seen
  loaded after its Set it is at exactly that minute.
- **While syncing Deepchart always follows.** Both run in sync or neither does.
- Only the Replay Manage windows get input. The chart windows are only looked at (pixels). Nothing takes the focus.

## How the replay time is known

1. The start date box of each Replay Manage window shows the minute. When it rolls over the replay is exactly at
   hh:mm:00 (`ReplayClock.cs`).
2. The seconds are read from pixels (`Screen/`): Deepdom's right-most time axis label and Deepchart's bar countdown.
   `ScreenTime.cs` holds the rules for when those may be trusted.

## Run

```bash
cd src/ReplaySync
dotnet run -- --start "06/01/2026 10:15" --auto   # clean start at that minute, then sync
dotnet run -- --auto                              # sync replays which are already running
dotnet run                                        # measure only
```

Keys: `A` toggles the correction, `P` plays in sync (turns the correction on), `Space` pauses both and turns it off,
`1` / `2` / `3` / `4` set both to x1 / x8 / x40 / x100, `Q` quits.

For measurements and test runs:

```bash
dotnet run -- --trace                                        # log every box, play state, speed and screen clock change
dotnet run -- --auto --plan "0:play,30:x40,90:x1,300:pause,305:quit"   # timed commands (play, pause, x<N>, quit)
dotnet run -- --speed-test Deepchart                         # paused: press x8, type 5, press x1, print box and slider
```

The date must be typed the way the date box shows it (e.g. `06/01/2026 10:15`).

## Files

| File | What |
|---|---|
| `Program.cs` | The console front end: options, status lines, keys. |
| `SyncController.cs` | The sync: polling, the correction rule, catch-up, the clean start. Shared with the desktop app. |
| `SyncOptions.cs` | The tuning values and their defaults. |
| `SyncSnapshot.cs` | The immutable state a front end shows. |
| `ReplayApp.cs` | One app's Replay Manage window: find it, read clock / playing / speed, press its buttons, type the date. |
| `ReplayClock.cs` | One replay clock: minute anchors, the bounds, the best guess. |
| `Screen/WindowCapture.cs` | Pixels of a window region through Windows Graphics Capture. |
| `Screen/ClockReaders.cs` | Find and read Deepdom's axis label and Deepchart's countdown. |
| `Screen/DigitReader.cs` | Reads the digits by matching rendered Segoe UI ones. |
| `Screen/ScreenTime.cs` | When a time read from pixels may be trusted. Pure logic. |
| `Screen/ScreenClocks.cs` | The thread which finds the windows and keeps reading them. |

## Options

| Option | Default | |
|---|---|---|
| `--auto` | off | start with the correction on (otherwise measure only) |
| `--margin <s>` | 0.5 | pause Deepchart when it is less than this many replay seconds behind Deepdom |
| `--hysteresis <s>` | 0.5 | resume it once it is margin + this behind |
| `--poll <ms>` | 200 | clock polling interval. Faster polling seemed to slow Deepdom down (see findings) |
| `--freeze-ms <ms>` | 500 | UI Automation timeout: an app which does not answer within this time counts as frozen |
| `--catch-up <s>` | 90 | when Deepchart is further behind, Forward it to the minute Deepdom shows |
| `--status <s>` | 5 | status line interval |
| `--verbose` | off | log every pause and resume |
| `--trace` | off | log every change of the date boxes, play states, speeds and screen clocks |
| `--plan "<steps>"` | | timed commands, seconds after the start: `play`, `pause`, `x<N>`, `quit` |
| `--start "<date>"` | | clean start at this minute |
| `--forward-wait <s>` | 5 | Forward gives no feedback: time to wait after it |
| `--load-timeout <s>` | 600 | how long to wait for each Reset, Set and load, not counting the time an app does not answer |

Quitting with `Q`, turning the correction off with `A`, or Ctrl+C resumes Deepchart if the tool was holding it.
Killing the process leaves Deepchart paused, which is the safe side.

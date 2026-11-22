# ReplaySync

Keeps the Deepchart replay from getting ahead of the Deepdom replay. Background, measurements and design decisions are
in [docs/findings.md](../../docs/findings.md).

## Rules

- **Deepdom is the reference and is never touched while syncing.** Only Deepchart is paused and resumed. Deepdom is
  only touched by a clean start (`--start`) and by the `P` / `Space` keys.
- **Deepchart must never be ahead.** Deepchart's replay time is taken as an upper bound, Deepdom's as a lower bound.
  Deepchart is paused when its upper bound is less than `--margin` seconds behind Deepdom's lower bound, and resumed
  when it is `--margin + --hysteresis` behind. Every uncertainty (no rollover seen yet, Deepdom frozen, crashed, paused
  by hand) therefore holds Deepchart back.
- **A changed Deepdom time counts only after its next rollover while playing.** Until then Deepchart is held, as text
  typed into the date box looks exactly like a new replay time. A clean start needs no such wait.
- **Frequent short pauses are fine**: Deepchart is only used for 5 minute bars and levels.
- Only the Replay Manage windows are touched (Play, Pause, date box, Set, Reset, Forward). Never the chart or DOM
  windows, which contain order buttons.

## How the replay time is known

The start date box of each Replay Manage window shows the live replay time while playing, with minute resolution. The
moment it rolls over (09:18 to 09:19) the replay is exactly at 09:19:00. Between rollovers the time is extrapolated
from the speed setting and each app's measured rate (Deepdom plays at about 0.65 to 0.9 of its speed setting). See
`ReplayClock.cs`.

## Run

```bash
cd src/ReplaySync
dotnet run -- --start "06/01/2026 10:15" --auto   # clean start at that minute, then sync
dotnet run -- --auto                              # sync replays which are already running
dotnet run                                        # measure only
```

Keys: `A` toggles the correction, `P` plays (Deepdom; Deepchart follows when the correction is on), `Space` pauses
both, `Q` quits.

The date must be typed the way the date box shows it (e.g. `06/01/2026 10:15`).

## Files

| File | What |
|---|---|
| `Program.cs` | The console front end: options, status lines, keys. |
| `SyncController.cs` | The sync: polling, the correction rule, catch-up, the clean start. Shared with the desktop app. |
| `SyncOptions.cs` | The tuning values and their defaults. |
| `SyncSnapshot.cs` | The immutable state a front end shows. |
| `ReplayApp.cs` | One app's Replay Manage window: find it, read clock / playing / speed, Play, Pause, Forward, Reset + Set. |
| `ReplayClock.cs` | Sub-minute replay time from a minute display: rollover anchors, rate learning, conservative bounds. |

## Options

| Option | Default | |
|---|---|---|
| `--auto` | off | start with the correction on (otherwise measure only) |
| `--margin <s>` | 0.5 | pause Deepchart when it is less than this many replay seconds behind Deepdom |
| `--hysteresis <s>` | 0.5 | resume it once it is margin + this behind |
| `--poll <ms>` | 200 | clock polling interval. Faster polling seemed to slow Deepdom down (see findings) |
| `--freeze-ms <ms>` | 500 | UI Automation timeout: an app which does not answer within this time counts as frozen |
| `--dom-rate <r>` | 0.6 | Deepdom's rate before it is measured |
| `--dom-safety <f>` | 0.8 | Deepdom's lower bound advances at its measured rate times this. Higher = Deepchart closer, less safe |
| `--catch-up <s>` | 90 | when Deepchart is further behind, Forward it to the minute Deepdom shows |
| `--status <s>` | 5 | status line interval |
| `--verbose` | off | log every pause and resume |
| `--start "<date>"` | | clean start at this minute |
| `--forward-wait <s>` | 5 | Forward gives no feedback: time to wait after it |
| `--load-timeout <s>` | 180 | how long to wait for Reset + Set to load |

Quitting with `Q`, turning the correction off with `A`, or Ctrl+C resumes Deepchart if the tool was holding it.
Killing the process leaves Deepchart paused, which is the safe side.

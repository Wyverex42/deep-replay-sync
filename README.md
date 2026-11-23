# deep-replay-sync

Keeps the **Deepchart** replay from getting ahead of the **Deepdom** replay.

Deepchart and Deepdom (Volumetrica Trading) each run their own tick replay. Trades are executed in Deepdom, with
Deepchart as support (5 minute bars, key levels, volume profile). Deepdom plays slower than its speed setting on a slow
machine, so without correction Deepchart runs ahead and shows what Deepdom has not played yet. ReplaySync drives both
replay windows through Windows UI Automation and pauses Deepchart whenever it gets close to Deepdom.

## Contents

| Path | What |
|---|---|
| [src/ReplaySync.App](src/ReplaySync.App) | The desktop app. |
| [src/ReplaySync](src/ReplaySync) | The sync engine and its console front end. |
| [src/ReplayProbe](src/ReplayProbe) | Read-only UI Automation probe: dumps what the two apps expose. Used to find the controls. |
| [scripts/uia.ps1](scripts/uia.ps1) | Small PowerShell UI Automation helper for poking at the replay windows by hand. |
| [docs/findings.md](docs/findings.md) | Everything learned about the two apps, the measurements, the sync algorithm and the open issues. |

## Requirements

- Windows 10 2004 or later (Windows 11 for capture without the yellow border), the .NET 10 SDK.
- Deepchart and Deepdom running, each with its **Replay Manage** window open.
- If the apps run as administrator, the tools must too (otherwise UI Automation sees nothing).

## Quick start

```bash
cd src/ReplaySync.App
dotnet run -c Release                             # the desktop app
```

Or in the console:

```bash
cd src/ReplaySync
dotnet run -- --start "06/01/2026 10:15" --auto   # clean start of both replays at that minute, then sync
dotnet run -- --auto                              # sync replays which are already running
dotnet run                                        # measure only, touch nothing
```

Keys: `A` toggles the correction, `P` plays in sync, `Space` pauses both, `Q` quits. See the header of
[src/ReplaySync/Program.cs](src/ReplaySync/Program.cs) or [src/ReplaySync/README.md](src/ReplaySync/README.md) for all
options.

```bash
cd src/ReplayProbe
dotnet run                    # dump the controls of both apps to probe-output/
dotnet run -- --watch 15      # and report which texts change while the replays play
```

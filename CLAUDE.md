# deep-replay-sync

Keeps the Deepchart replay from getting ahead of the Deepdom replay (Volumetrica Trading apps) by driving their
"Replay Manage" windows through Windows UI Automation (FlaUI.UIA3).

- Read [docs/findings.md](docs/findings.md) first: the apps' controls, behaviour, measurements, the sync algorithm and
  the open issues.
- `src/ReplaySync` is the sync tool, `src/ReplayProbe` a read-only probe, `scripts/uia.ps1` a manual helper.
- Build: `dotnet build DeepReplaySync.slnx` (.NET 10, Windows only).

## Hard rules

- **Deepchart must never be ahead of Deepdom.** Any uncertainty must hold Deepchart back, never let it run.
- **Never touch Deepdom's playback while syncing** (the user trades there). Only a clean start may Reset/Set/Play it.
- **Only touch the Replay Manage windows.** The chart and DOM windows contain live order buttons.
- Find elements by AutomationId (real `x:Name`s), never by class name (obfuscated).
- Testing against the running apps changes the user's replay: say what will be pressed before doing it.

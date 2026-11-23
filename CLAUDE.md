# deep-replay-sync

Keeps the Deepchart replay from getting ahead of the Deepdom replay (Volumetrica Trading apps) by driving their
"Replay Manage" windows through Windows UI Automation (FlaUI.UIA3).

- Read [docs/findings.md](docs/findings.md) first: the apps' controls, behaviour, measurements, the sync algorithm and
  the open issues.
- `src/ReplaySync` is the sync engine and its console, `src/ReplaySync.App` the desktop app, `src/ReplayProbe` a read-only probe, `scripts/uia.ps1` a manual helper.
- Build: `dotnet build DeepReplaySync.slnx` (.NET 10, Windows only).

## Hard rules

- **At x1 Deepchart must never be ahead of Deepdom.** Any uncertainty must hold Deepchart back, never let it run.
  Above x1 (fast forward, never traded) it is kept close and may be a little ahead; slowing to x1 holds it until
  Deepdom is certainly ahead.
- **Never touch Deepdom's playback while syncing** (the user trades there). Only the master Play / Pause button,
  the speed buttons, a clean start and closing the app (pauses both) may press it: all are the user's own clicks.
- **Only touch the Replay Manage windows.** The chart and DOM windows contain live order buttons. Their pixels may be
  read through Windows Graphics Capture (the clocks with seconds), but never send them input, focus or messages.
- **Never take the focus**: UI Automation clients must set `AutoSetFocus = 0`, or every press activates the target.
- Find elements by AutomationId (real `x:Name`s), never by class name (obfuscated).
- Testing against the running apps changes the user's replay: say what will be pressed before doing it.

## Testing

Keep testing proportionate. End-to-end runs against fakes take real time (20-80 s each), so run only what the change
needs:

| Change | Testing |
|---|---|
| UI, text, icon, docs | Build only |
| Bug fix | One reproduction before and after, 1-2 min |
| Rules that decide when Deepchart may play | Also one safety run (Deepchart never ahead), about 5 min |
| Anything longer | Ask the user first |

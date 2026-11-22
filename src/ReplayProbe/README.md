# ReplayProbe

Read-only UI Automation probe. It lists what Deepchart and Deepdom expose to UI Automation, to find out whether an
external tool can drive both replays (play / pause / speed) and read their replay clocks. It never clicks anything.

## Run

Start Deepchart and Deepdom, open a replay in each, then:

```bash
cd src/ReplayProbe
dotnet run                    # dump both apps
dotnet run -- --watch 10      # dump, then watch for 10 s which texts/values change (run while the replay plays)
dotnet run -- --raw           # raw UIA view: more elements, including ones the control view hides
dotnet run -- deepdom         # only the processes whose name contains "deepdom"
```

If the apps run as administrator, run the probe as administrator too, otherwise the trees come back empty.

## Output

- Console: for each top-level window, the controls that look like replay controls (an actionable control with a
  keyword such as play, pause or speed in its id, name or tooltip) and every text that looks like a time or date,
  each with a path to find it again.
- `probe-output/<process>-w<n>.txt`: the full tree of each window. Identity comes first and geometry last on each
  line, so the dumps of two runs (restart the apps in between) can be diffed to check that ids stay the same.

## What to look for

- Replay buttons with a non-empty `id` (AutomationId) and the `Invoke` or `Toggle` pattern: they can be pressed
  reliably without the mouse.
- A speed control with `RangeValue`, `SelectionItem` or `ExpandCollapse`.
- With `--watch`, an element that changes about once per poll while the replay plays: that is the replay clock.

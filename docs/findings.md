# Findings

What we learned about Deepchart and Deepdom while building the replay sync, as of 2026-11-22. Observed on one machine
(an old, slow laptop) with Deepchart and Deepdom installed in `C:\Program Files\Volumetrica Trading\`.

## Goal and requirements

- Trades are executed in **Deepdom**. **Deepchart** is support only: 5 minute bars, key levels, volume profile.
- Both apps have their own, independent tick replay. They must play the same market time.
- **Deepchart must never be ahead of Deepdom**: seeing even a few seconds of the future in Deepchart affects trading
  decisions. Being a little behind is acceptable.
- **Deepdom's playback must not be disturbed** while trading: the sync only pauses and resumes Deepchart.
- Frequent short pauses of Deepchart are fine.
- Both replays run at the same speed setting (x1 so far).

## The apps

- Both are **.NET 10 WPF** apps (`Microsoft.WindowsDesktop.App` in their `runtimeconfig.json`). Process names
  `Deepchart` and `Deepdom`. A `VolumetricaBridge` process runs alongside.
- Some class names are obfuscated and will likely change between versions (`LocalExplorer`, `ControllerWriter`,
  `SchemaConfiguration`, `EvaluatorListCollection`, `SimpleResolver`, `DetailedMemberServer`). The **AutomationIds
  are real `x:Name`s** and look stable: always find elements by AutomationId, never by class name.
- **Deepdom has no custom indicator support**: no `VolSysAPI.dll` / `VolumetricaCore.dll` in its folder and no
  `Indicators` folder in `Documents\Deepdom`. Deepchart does have it (indicators could read its exact replay time),
  but that only helps on the Deepchart side, which is not the bottleneck.

## The Replay Manage window

Both apps have a top-level window named **`Replay Manage`** with nearly the same controls (UI Automation control view,
AutomationIds):

| Element | AutomationId | Notes |
|---|---|---|
| Symbols list | `lbxSymb` | Deepchart had MNQ-CME and NQ-CME, Deepdom NQ-CME |
| Select Symbols | `btnSelSymbol` | |
| Start date picker | `dtp_IniDt` → child Edit `PART_Txt` | **also the live replay clock**, see below. Value pattern, settable |
| Date picker button | `dtp_IniDt` → `PART_Btn` | |
| Forward | `btnForward` | **Deepchart only**. No name, icon only |
| Speed slider | `tkbSpeed` | RangeValue 1..400 |
| Speed number box | `nudSpeed` → Edit `PART_Txt` | Value pattern, e.g. `1` |
| Speed presets | buttons named `x1`, `x8`, `x40`, `x100`, `x200`, `x400` | **no AutomationId**, find by name |
| Market depth (high performance) | `ckbIncludeBook` | **Deepchart only**, Toggle |
| Reset / Set / Pause / Play | `btnReset`, `btnSet`, `btnPause`, `btnPlay` | Invoke pattern, work without focus |

### Button states

| State | Play | Pause | Set | Reset |
|---|---|---|---|---|
| Loaded, paused | enabled | disabled | disabled | enabled |
| Playing | disabled | enabled | disabled | enabled |
| After Reset | disabled | disabled | enabled | disabled |

So **`btnPause` enabled ⇔ playing**, and **`btnPlay` enabled ⇔ loaded and paused** (used to detect the end of
loading after Set).

### The replay clock

- The start date box (`dtp_IniDt/PART_Txt`) shows the **current replay time while playing**, format
  `06/01/2026 09:19` (`dd/MM/yyyy HH:mm` or `MM/dd/...`: both apps use the same format, which is all that matters).
- **Minute resolution only.** It was the only element in either app that changed during playback (ReplayProbe
  `--watch`).
- When the replay is paused, or after text was typed into it, it shows the last value / the typed text until the
  next update. Typed text is not a replay position.
- The moment it changes from one minute to the next (a **rollover**), the replay is at exactly `hh:mm:00`. This gives
  sub-second precision once per replay minute (to within one poll interval times the speed).

### Moving in time

- **Deepdom**: the only way, forward or back, is **Reset → type the date → Set**. Set is disabled until Reset was
  pressed; typing a date alone does not enable it. After Set the data loads: **69 s and 171 s** observed until Play
  was enabled. **Deepdom sometimes crashes during the Set cycle** (user report; not observed by us).
- **Deepchart backwards**: Reset → date → Set (reloads the data, slow). Not tested through automation yet.
- **Deepchart forwards**: type a later date into the date box, press **`btnForward`**. The chart jumps forward.
  There is **no feedback in the Replay Manage window** (no popup, no state change): the chart just moves. With a date
  earlier than the current position, Forward does nothing. Typing via `ValuePattern.SetValue` is enough, no focus
  change or Enter needed.
- After a Forward of 23 minutes, a Play pressed ~3 minutes later timed out (500 ms UI Automation timeout): Deepchart
  was busy. Hence Play/Pause are now retried until the state changes (`ReplayApp.EnsurePlaying`).

## What is not accessible

- The **chart and DOM contents** (axes, bars, countdown timer, time & sales, ladder) are drawn into native child
  windows (`HwndHost` → `HwndWrapper`) with **no UI Automation children**, also in the raw view. The time axis labels
  and Deepchart's bar countdown can not be read. OCR on screenshots would be the only way. Rejected: the Deepdom axis
  labels give the time of past bars, not the current time (pixel distance would have to be converted to time, which
  breaks on zoom/scroll), tiny fonts make OCR error prone, and the window must be visible. Deepchart's countdown would
  give 1 s resolution, but Deepchart is not the side that needs it.
- The main DOM and chart windows contain **order buttons** (`btnBuyMkt`, `btnSellMkt`, `btnBuyLmt`, ...). The tools
  must never touch anything outside the Replay Manage windows.

## UI Automation behaviour

- UI Automation requests are answered by the **target app's UI thread**. A busy or frozen app makes the calls block,
  by default for several seconds. ReplaySync sets `ConnectionTimeout` and `TransactionTimeout` to 500 ms so a frozen
  Deepdom does not stall the loop (Deepchart must be paused *while* Deepdom is frozen).
- A timeout throws. `ElementNotAvailableException` or a dead process means the window is gone (reconnect); any other
  exception with a live process is treated as "not responding" and the elements are kept.
- If the apps run elevated, the tools must run elevated too.
- **Polling may load Deepdom.** At 50 ms polling Deepdom played at 0.65x and once stopped answering UI Automation for
  ~15 s; at 200 ms it played at 0.85 to 0.89x with no freezes. Two runs only, different market periods: suggestive,
  not proven. Default is now 200 ms.

## Measurements (x1, Deepdom on the slow laptop)

| Run | Observation |
|---|---|
| Uncorrected, mid-session | Deepdom at **0.66x**, gap grew from 89 s to 111 s in ~90 s |
| Uncorrected, clean start at 09:45 | Deepdom needed 66.7 s, 68.5 s, then **106.6 s** for one replay minute (the 15 s freeze). Deepchart 6 s ahead after 1 minute, 60 s after 4 |
| Deepchart's own rate | 0.93 to 1.01 |
| Symmetric correction, 3 s tolerance (v1) | Kept within ~3 s, Deepchart in 9 s play / 5 s pause cycles. Rejected: Deepchart could be up to 3 s ahead |
| One-sided correction (v2, current) | Deepdom 0.85 to 0.89x, **never ahead at any Deepdom rollover** (worst −9.9 s). Deepchart's lag varies from ~1 s just after a Deepdom rollover to ~13 s late in Deepdom's minute (cost of `--dom-safety 0.8`) |

Deepdom's lag is **bursty** (stalls and freezes), not a constant rate.

## The sync algorithm (ReplaySync)

1. **Clock per app** (`ReplayClock`): each rollover is an exact anchor. Between anchors the time advances by
   wall time × speed × measured rate. The rate is learned between consecutive rollovers (EMA). Any other change of
   the displayed minute (Set, Forward, skipped minutes) is a jump: the clock is unanchored until the next rollover.
2. **Conservative bounds**: Deepchart's clock is an **upper bound** (rate × 1.05; before an anchor: end of the
   displayed minute). Deepdom's is a **lower bound** (rate × `--dom-safety`; before an anchor: start of the
   displayed minute). Wall time during which Deepdom did not answer is **not credited** to its time (it counts only
   for rate learning).
3. **Rule**: pause Deepchart when `chartUpper > domLower − margin`; resume when
   `chartUpper ≤ domLower − margin − hysteresis`. This single rule also covers a frozen, crashed or user-paused
   Deepdom: its lower bound stops moving, so Deepchart is held.
4. **Only Deepchart is resumed by the tool, and only if the tool paused it.** A Deepchart paused by hand stays paused.
   If Deepchart is played by hand while it must be held, it is paused again.
5. **Catch-up**: when Deepchart is more than `--catch-up` seconds behind, it is paused and Forwarded to the minute
   Deepdom displays (safe: Deepdom is at or past that minute) and anchored there. It then lands up to a minute behind
   and closes the gap at its full speed.
6. **Clean start** (`--start`): pause both, Deepdom Reset → date → Set, Deepchart Forward (or Reset → Set if the date
   is not ahead), wait until both have Play enabled (detecting a Deepdom crash), wait `--forward-wait`, then Play
   Deepdom first and Deepchart second, and **anchor both clocks at the start minute** so there is no unprotected
   first minute.

## Open issues and ideas

- **Untested**: the clean start since `EnsurePlaying` was added; Deepchart's Reset → Set path; speeds above x1 (at
  high speed one poll is many replay seconds, and minutes can be skipped, which unanchors the clock).
- **Speeds are not synced**, only a warning is printed when they differ. Could be set through `nudSpeed/PART_Txt` or
  the preset buttons (untested).
- **`--dom-safety` trade-off**: closer following (higher value) vs the risk that a short Deepdom stall (below the
  freeze timeout) lets Deepchart get briefly ahead.
- **Deepdom crash mid-session**: after a restart Deepdom is at an earlier time; Deepchart is held until Deepdom passes
  it, which can be long. Deepchart can only go back with Reset → Set.
- **Catch-up precision**: Forward goes to Deepdom's displayed minute, up to 60 s behind its real position.
- **Forward completion** is not detectable; a fixed wait is used.
- **Date format** is parsed with the current culture, then a few fixed formats. Typing a date for `--start` must
  match what the box shows.
- **Structure for a UI**: the correction rule and the clean start live in the top-level `Program.cs` together with
  the console output and keys. For a UI, move them into a `SyncController` class (state, events for rollovers,
  pauses, freezes and status) and keep `ReplayApp` and `ReplayClock` as they are.

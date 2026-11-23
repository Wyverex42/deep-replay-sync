# Findings

What we learned about Deepchart and Deepdom while building the replay sync, as of 2026-11-22. Observed on one machine
(an old, slow laptop) with Deepchart and Deepdom installed in `C:\Program Files\Volumetrica Trading\`.

## Goal and requirements

- Trades are executed in **Deepdom**. **Deepchart** is support only: 5 minute bars, key levels, volume profile.
- Both apps have their own, independent tick replay. They must play the same market time.
- **Deepchart must never be ahead of Deepdom**: seeing even a few seconds of the future in Deepchart affects trading
  decisions. Being a little behind is acceptable.
- **Deepdom's playback must not be disturbed** while trading: the sync only pauses and resumes Deepchart. Closing
  the desktop app pauses both.
- Frequent short pauses of Deepchart are fine.
- Both replays run at the same speed setting. Trading happens only at x1. Above x1 (fast forward to a level) being
  a little ahead is acceptable, but slowing back to x1 must leave Deepdom certainly ahead.

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
| Speed presets | buttons named `x1`, `x8`, `x40`, `x100`, `x200`, `x400` | **no AutomationId**, find by name. Invoke works without focus, also while paused |
| Market depth (high performance) | `ckbIncludeBook` | **Deepchart only**, Toggle |
| Reset / Set / Pause / Play | `btnReset`, `btnSet`, `btnPause`, `btnPlay` | Invoke pattern, work without focus |

### Button states

| State | Play | Pause | Set | Reset |
|---|---|---|---|---|
| Loaded, paused | enabled | disabled | disabled | enabled |
| Playing | disabled | enabled | disabled | enabled |
| After Reset | disabled | disabled | enabled | disabled |
| Loading after Set | disabled | disabled | disabled | disabled |

Seen on Deepchart: Forward is enabled while loaded (playing or paused) and disabled in the other two states. While
loading all five are disabled, so a Set which is still enabled was not taken.

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
  was enabled. **Deepdom sometimes crashes during the Set cycle** (user report). The Windows event log
  has a crash at 2026-11-22 18:02 which is probably one: an unhandled `Unable to download historical data: historical
  server connection failed` in a click handler of Deepdom.
- **Deepchart backwards**: Reset → date → Set (reloads the data, slow). **The Reset alone can take over a minute**
  (2026-11-22: Set was still disabled after 2 minutes, half of which Deepchart did not answer).
- **Deepchart forwards**: type a later date into the date box, press **`btnForward`**. The chart jumps forward.
  There is **no feedback in the Replay Manage window** (no popup, no state change): the chart just moves. With a date
  earlier than the current position, Forward does nothing. Typing via `ValuePattern.SetValue` is enough, no focus
  change or Enter needed.
- After a Forward of 23 minutes, a Play pressed ~3 minutes later timed out (500 ms UI Automation timeout): Deepchart
  was busy. Hence Play/Pause are now retried until the state changes (`ReplayApp.EnsurePlaying`).

## What is only accessible as pixels

- The **chart and DOM contents** (axes, bars, countdown timer, time & sales, ladder) are drawn into native child
  windows (`HwndHost` → `HwndWrapper`) with **no UI Automation children**, also in the raw view.
- **Deepdom's time axis** is the one fine clock of Deepdom. Its **right-most label sits at a fixed position and
  advances every second**, showing the replay time minus a constant which depends on the zoom (about 3.8 s at 28 s
  between labels, 66 px). The constant is calibrated at every minute change. An earlier session rejected the axis as
  "past bars only, breaks on zoom": recording it while playing showed otherwise.
- **Deepchart's bar countdown** shows `bar length - seconds into the bar`, rounded up: `00:24` on 1 minute bars is
  second 36.0 to 36.99. It is redrawn up to ~0.6 s late.
- Both are read by matching rendered Segoe UI digits: 261 of 261 recorded frames right. The Windows OCR engine read
  only 60 % of the small axis labels.
- **Capture**: use Windows Graphics Capture and copy only the needed strip. `PrintWindow` took 135 ms of Deepdom's
  UI thread per image. Copying whole windows from the GPU cost ~20 % of a CPU core per window, the strips ~1 %.
- **Deepdom's log files are encrypted**: left alone.
- The main DOM and chart windows contain **order buttons** (`btnBuyMkt`, `btnSellMkt`, `btnBuyLmt`, ...). The tools
  never send anything outside the Replay Manage windows.

## UI Automation behaviour

- UI Automation requests are answered by the **target app's UI thread**. A busy or frozen app makes the calls block,
  by default for several seconds. ReplaySync sets `ConnectionTimeout` and `TransactionTimeout` to 500 ms so a frozen
  Deepdom does not stall the loop (Deepchart must be paused *while* Deepdom is frozen).
- A timeout throws. `ElementNotAvailableException` or a dead process means the window is gone (reconnect); any other
  exception with a live process is treated as "not responding" and the elements are kept.
- **A timed out press or SetValue is often still carried out** once the app continues. Look at the state before
  pressing again, and never take it for "button disabled".
- **While an app hangs** (tried with a fake WPF app): `IsEnabled` throws a timeout, the Value pattern throws
  `PatternNotSupportedException`, finding its elements fails, and the search of the desktop's windows can time out as
  a whole. So a failed read is never "paused" or "disabled".
- If the apps run elevated, the tools must run elevated too.
- **Polling may load Deepdom.** At 50 ms polling Deepdom played at 0.65x and once stopped answering UI Automation for
  ~15 s; at 200 ms it played at 0.85 to 0.89x with no freezes. Two runs only, different market periods: suggestive,
  not proven. Default is now 200 ms.
- **`AutoSetFocus` is on by default** and makes every Invoke and SetValue activate the target window and move the
  keyboard focus into it. With presses every 1.5 s the PC was unusable. `IUIAutomation2.AutoSetFocus = 0` fixes it,
  presses work the same.
- In WPF, `Keyboard.Focus` and `FocusManager.SetFocusedElement` on an inactive window activate it.

## Measurements (x1, Deepdom on the slow laptop)

| Run | Observation |
|---|---|
| Uncorrected, mid-session | Deepdom at **0.66x**, gap grew from 89 s to 111 s in ~90 s |
| Uncorrected, clean start at 09:45 | Deepdom needed 66.7 s, 68.5 s, then **106.6 s** for one replay minute (the 15 s freeze). Deepchart 6 s ahead after 1 minute, 60 s after 4 |
| Deepchart's own rate | 0.93 to 1.01 |
| Symmetric correction, 3 s tolerance (v1) | Kept within ~3 s, Deepchart in 9 s play / 5 s pause cycles. Rejected: Deepchart could be up to 3 s ahead |
| One-sided correction (v2) | Deepdom 0.85 to 0.89x, **never ahead at any Deepdom rollover** (worst −9.9 s). Deepchart's lag varies from ~1 s just after a Deepdom rollover to ~13 s late in Deepdom's minute (cost of `--dom-safety 0.8`) |
| Recording, 85 s at 10:27 | Deepdom **0.57x**, Deepchart 0.96x. v2 would have assumed at least 0.69x after a 0.86x minute: **v2 was not safe** |
| Seconds from the screen (v3, current), 150 s + 48 s live | Deepdom 0.57x. **Never ahead** (smallest gap 1.8 s), median lag **3.8 s** once calibrated, 5.8 s before the first minute change. No focus taken by either app |

### Above x1 (2026-11-22, `ReplaySync --trace`, speeds set by hand)

| Setting | Deepdom (box) | Deepchart (box) |
|---|---|---|
| x8 | 0.55 of nominal (~14 s per replay minute) | ~1.0 |
| x40 | 0.58 (~2.7 s per minute) | 0.99 |
| x100 | 0.5 to 0.65 (~1.2 s per minute) | 0.92 |
| x200 | **stopped answering UI Automation for 90 s** at once | |
| x400 | ~0.33, minutes skipped in the box (14:13 → 14:15) | |

- **After the x400 burst Deepdom's chart stopped drawing** (axis stuck at 14:11:56) while its box went on to 15:07
  and played at 0.8 to 0.97 of nominal: drawing is what slows Deepdom, and **the box is not proof of what Deepdom
  shows**. It did not recover at x1 and later stopped answering for 84 s again. A rule that follows the box when
  the axis is stuck would let Deepchart run up to an hour ahead, also at x1.
- Right after x400 → x1, Deepdom's box still rolled over 3 s later: a speed change is not instant.
- The axis reader kept up at x40 (a new label every 200 ms poll).
- Above x100 is not worth it on this laptop; x200 and x400 damaged the Deepdom session.
- **Second run after a Deepdom restart, x100 at most** (both apps, ~30 s per speed): Deepdom 0.55 at x8, 0.51 at
  x40, 0.45 at x100; Deepchart 0.97 at x40, 0.98 at x100. Deepchart would gain ~50 replay s per wall second at
  x100. Deepdom's axis followed its box all the way (0 to 50 s past the box minute), and after x100 → x1 it was at
  x1 pace within 1.5 s (2 s of axis per ~3.2 s wall, 0.62x). No freeze, no skipped minutes.

Deepdom's lag is **bursty** (stalls and freezes) and its rate varies: never extrapolate it.
At 0.57x Deepchart has to be paused about 40 % of the time whatever the algorithm: it plays 1.7 times as fast.

## The sync algorithm (v3)

1. **Clock per app** (`ReplayClock`): each rollover of the minute display is an exact anchor. Any other change of the
   displayed minute (Set, Forward, skipped minutes) is a jump: the clock is unanchored until the next rollover.
2. **Deepdom's lower bound** moves only with what was seen: `label + offset - step` from the axis, where
   `offset = minute - label` just after a rollover, or after Play from a minute it was loaded at. Before that the label itself, when it is under played data. It
   is never extrapolated. Without a readable axis it only moves at rollovers.
3. **Deepchart's upper bound** comes from the moment the countdown changes (or its settled value while paused) and
   advances at 1.02 x the speed in between. After failed reads it is credited as playing for the whole gap.
4. **Distrust**: a changed Deepdom minute counts only after a rollover while playing (typed text looks the same),
   except where a clean start Set it and it was then seen loaded (a reset replay can not load without a Set). A
   reset holds at once, a label jumping forward waits for a rollover, a label moving back while playing is a scrolled
   chart. The rules are in `Screen/ScreenTime.cs`.
5. **Rule**: pause Deepchart when `chartUpper > domLower − margin`; resume when
   `chartUpper ≤ domLower − margin − hysteresis`. While syncing it always follows, also after a pause by hand.
6. **Catch-up**: when Deepchart is more than `--catch-up` seconds behind, it is paused and Forwarded to the minute
   Deepdom displays (safe: Deepdom is at or past that minute) and anchored there.
7. **Master Play**: Deepdom plays, the sync is on, the rule starts Deepchart. **Master Pause**: Deepchart, then
   Deepdom, then the sync is off.
8. **Clean start**: pause both, Deepdom Reset → date → Set, Deepchart Forward (or Reset → Set if the date is not
   ahead), wait until both have Play enabled (detecting a Deepdom crash), wait `--forward-wait`, anchor both
   clocks at the start minute, then Play Deepdom. The rule starts Deepchart: pressed right away it was ahead when
   Deepdom froze at its start. Every step is repeated until its result
   is seen, and time in which an app does not answer does not count against a timeout.
9. **Closing the app**: pause both until both are seen paused for 1 s at the same time (30 s at most, then a warning).
10. **Speed** (the app's x1 / x8 / x40 / x100 buttons or a typed 1 to 100, console keys 1 to 4): a user click like
    Play. Going up Deepdom is set first, going down Deepchart, so Deepchart is never the faster one. A preset is
    pressed, any other value is typed into `nudSpeed/PART_Txt` (taken without Enter: the slider follows). Each app
    is pressed again until its box shows the speed.
11. **Fast forward** (both above x1): Deepchart is kept within ±`FastLeadSeconds` (1) wall second of playback of
    Deepdom (±40 s at x40, ±100 s at x100), on the best guesses. No catch-up. A box that skips minutes while
    playing fast, no further than could have been played, is progress, not a jump.
12. **Slowing to x1** while syncing: Deepchart is held, Deepdom plays on fast until the x1 worst-case rule says it
    is past Deepchart (30 s at most), then Deepdom and Deepchart go to x1 and the x1 rule takes over.
13. **Stuck Deepdom chart**: when Deepdom's box changes minute twice while playing and its axis label stands still,
    Deepchart is held until the label moves (at every speed; also a chart scrolled back).

## How it was verified

- The real controller, clocks and trust rules against simulated apps (bursty rates, zooms, drags, scrolling,
  minimizing, typed dates, Sets, hand presses), 1800 hours: never ahead at the default margin. With a margin below
  0.5 an adversarial zoom case got ahead, so the app does not accept less.
- Clean start and closing against fake WPF replay windows which freeze at each step (2 to 20 s, and repeatedly) or
  exit: one Reset, Set and Play each, both paused after closing or a warning which names the app.
- Not tried on the real apps: clean start with the retries, closing, catch-up, zooming or scrolling while playing,
  5 minute bars, other themes and display scalings (only dark theme at 125 %). The CPU and display fixes made after
  the last live run were only checked against the paused apps.

## Open issues and ideas

- **Speeds above x1** are synced (rules 10 to 13), verified on the real apps on 2026-11-23: 10:00 → 10:52 with
  x8 / x40 / x100 / x1, never ahead at x1 (worst bound while Deepchart played −0.6 s, through a 15 s Deepdom freeze),
  Deepdom past the held Deepchart 0.2 s after slowing. Fast forward is coarse: −180 s to +60 s at x100 (one poll is
  20 s of replay). The cap is x100: x200 froze Deepdom.
- **Deepdom's chart can stop while its box plays on.** Seen twice on 06/01/2026, both times at axis label
  **14:11:56**: after an x400 burst, and again after a clean x100 → x1 slowdown (window responsive). The same label
  twice suggests the data there rather than a freeze. The sync now holds Deepchart when the box changes minute twice
  while the axis label stands still (also when the chart is only scrolled back).
- **Speeds set by hand** in the apps are followed (the mode comes from the speeds read), but not mirrored to the
  other app: a warning is shown when they differ.
- **First minute after starting the tool** has ~2 s more lag: the axis offset is not calibrated yet.
- **Bars under one minute** in Deepchart make the countdown useless (safe, but ~30 s of lag).
- **Deepdom crash mid-session**: after a restart Deepdom is at an earlier time; Deepchart is held until Deepdom passes
  it, which can be long. Deepchart can only go back with Reset → Set.
- **Catch-up precision**: Forward goes to Deepdom's displayed minute, up to 60 s behind its real position.
- **Forward completion** is not detectable; a fixed wait is used.
- **Date format** is parsed with the current culture, then a few fixed formats. Typing a date for `--start` must
  match what the box shows.
- **Console keys against the rules**: `Q`, `A` (off) and Ctrl+C resume a held Deepchart, which may then get ahead.
- **Text typed into Deepchart's box** is still believed: an earlier date lowers its upper bound.
- **Text typed into Deepdom's box before the tool started** is believed unless Deepdom is reset or loading.
- **Deepchart can get ahead while it does not answer**: nothing can pause it then. It is held afterwards.
- **Killing the tool** (or signing out of Windows, which does not wait for the pause) while Deepchart plays leaves it
  playing unguarded: there is no watchdog.
- **Startup** costs a busy CPU core for ~20 s (JIT). ReadyToRun would shorten it.

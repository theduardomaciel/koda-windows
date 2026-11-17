# Mini player sizing

How the compact overlay picks its size, and why the width follows the screen it is
on. The short version:

- The compact card is **425 DIP wide** when the screen has room for it.
- The expanded card keeps a **6:11 portrait shape** (the Apple Music mini player's
  proportions) and grows only downward/upward from the same width.
- A screen too short for the expanded card gets a **narrower compact card from the
  start**, so opening a panel never changes the width.
- **360 DIP** is the hard floor: below it the transport, Volume/More and
  Lyrics/Queue clusters collide.

## Where the values live

| Value | File | What it does |
|---|---|---|
| `PreferredCardWidth` | `src/Koda/MiniPlayerWindow.cs` | The width the mini player opens at on a normal display (`425`). |
| `CompactCardHeight` | Same | The collapsed card's height (`200`). |
| `ExpandedAspectWidth` / `ExpandedAspectHeight` | Same | The expanded card's shape (`6` / `11`). Height = width × 11 / 6. |
| `MinCardWidth` | Same | The layout floor (`360`). |
| `FitCardWidth` | Same | Picks the width for the display (see the formula below). |
| `_widthFit` timer | Same | Re-fits the card 250 ms after a move to another display settles. |
| Track strip, panel, seek bar, controls | `src/Koda/Pages/MiniPlayerPage.xaml` | Row heights add up to the window size; the panel row is `0` until expanded. |

## How the width is chosen

```text
workHeightDIP = display work area height / DPI scale
fitWidth      = workHeightDIP * 6 / 11
cardWidth     = clamp(min(preferredWidth, fitWidth), 360, 425)
expandedHeight = cardWidth * 11 / 6
```

The work area is the screen minus the taskbar, so the expanded card never sits
under it. The card width is fixed for both states; expanding only adds height.

Results for a 1920 × 1080 display (1032 px of work area) at common scales:

| Scale | Work area (DIP) | Fit width | Card width | Expanded height |
|---|---|---|---|---|
| 100 % | 1032 | 563 | 425 | 779 DIP |
| 125 % | 825.6 | 450 | 425 | 779 DIP (974 px) |
| 150 % | 688 | 375 | 375 | 688 DIP (1032 px) |
| 200 % | 516 | 281 | 360 (floor) | 516 DIP (ratio gives; width stays at the floor) |

A 1280 × 800 display at 100 % (752 px of work area) gives a 410 DIP card and a
752 DIP expanded height.

## Current approach: fit the width to the screen

The width is computed for the current display when the card is created, after a
move settles, and whenever the card expands or collapses. Compact and expanded
share one width.

### Trade-offs

**Good**

- Opening a panel never changes the card's width. The window grows vertically
  from the compact card, which is the interaction the design is about.
- The expanded card always fits the work area at 6:11 on the display it is on.
- Moving the compact card to a shorter display re-fits it before the next open,
  so the first expansion on that screen still only adds height.
- One width means the track header and the control row keep their alignment
  through the transition.

**Costs**

- On a short display the compact card is intentionally narrower than the 425
  DIP preference, with less room for the cover and title. That is the price of
  keeping the expanded shape on screen.
- Dragging the compact card to another display changes its width after a 250 ms
  settle. It is a visible change while the window is being placed, and the
  resize cannot happen mid-drag.
- The width is no longer one fixed design value, so screenshots and manual
  checks differ between 100 %, 125 % and multi-monitor setups.
- Below 360 DIP the controls need more room than the width allows. There the
  ratio has to give (`FitCardWidth` floors the width and `TryResizeCard` caps the
  height), which is the only case where the 6:11 shape is not exact.
- A move while expanded does not re-fit immediately; the compact size catches
  up when the card collapses. Re-fitting an expanded card mid-session would move
  the panel the listener is reading.

## Previous approach: keep a fixed width and narrow on expand

The card was a fixed 450 DIP (then 400) wide. When the 6:11 expanded height did
not fit the work area, the width was narrowed at the moment of expansion so the
ratio survived.

### Trade-offs

**Good**

- The compact card is always the same width, at every scale and on every
  display. The design values do not depend on the monitor.
- No resize happens while the card is being dragged.
- Simpler: one size at construction, and one correction only when needed.

**Costs**

- Expanding can visibly narrow the card, so the window changes width at the
  exact moment the listener asks for the panel. On a small screen every open
  performs that horizontal jump.
- The compact card can be wider than the screen can support, which makes the
  first expansion feel like a correction rather than a continuation.
- The expanded shape is only guaranteed after the expansion; the compact card
  does not advertise the size the panel will take.

## Why not the other options

- **Keep 425 DIP everywhere and let the window overflow.** The expanded panel or
  the transport would go off screen, especially near the taskbar.
- **Keep 425 DIP and clamp the height only.** The 6:11 shape would flatten into
  a wide card, which is the opposite of the reference.
- **Go below 360 DIP.** The control row needs about 352 DIP: 32 DIP of card
  padding, 24 DIP of control padding, 80 DIP for Volume/More, 136 DIP for the
  transport and 80 DIP for Lyrics/Queue. A narrower floor would require moving
  Volume/More into an overflow menu.

## Position behaviour

- Expanding keeps the top edge when the taller card fits; otherwise the bottom
  edge stays put and the card grows upward.
- Collapsing restores the compact card's remembered position, clamped to the
  current work area.
- The caption group and its drag passthrough are re-measured after every card
  resize.
- Opening a panel freezes the track row at its measured compact height. The
  collapsed card's star row is a couple of pixels taller than the 80 DIP cover,
  so using a fixed 80 DIP in the expanded card made the cover and title jump up;
  carrying the measured height keeps them still.

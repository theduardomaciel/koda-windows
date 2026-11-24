# Lyrics memory and visual investigation

This is the running record of the lyrics memory-leak investigation and the visual
regressions that followed it. It covers the Windows port, the Spicy/Better Lyrics
CSS reference, the Android implementation, and the profiling runs.

Related reads:

- [The Expressive lyrics look](lyrics-expressive.md) - the motion model and DropShadow glow.
- `C:\koda-profiles\AUTOMATED-PROFILE-RESULTS.md` - the raw A/B tables.
- `spicy.css` - the authoritative Spicy style.
- Android `SyncedLyricsView.kt` - useful for architecture, not for Spicy values.

## TL;DR

- The original leak was **per-frame XAML RenderTransform on every word and letter**.
  XAML re-rasterized the text surfaces continuously; the managed heap stayed small,
  so GC did not run until memory had already reached 800 MB-1.1 GB.
- Moving word/letter motion to composition visuals (`Visual.Scale`,
  `Visual.CenterPoint`, and the enabled `Translation` property) fixed it.
  - Motion-only: 842 MB -> **244 MB**, flat.
  - Default Expressive look: 1,126 MB -> **344 MB**.
  - Full mixed scenario: **377 MB**.
- After that, the remaining growth was texture/mask churn and cache warm-up, not the
  per-frame motion. Glow masks/twins and the first song's word pool accounted for it.
- The visual regressions were caused by **virtualization and placeholders**, not by
  composition motion. A single-text placeholder cannot reproduce Spicy's per-word
  and per-letter resting layout.
- `435686e` already had the correct visual architecture: every synced line was
  word/letter-level from load, with no placeholder or rich window. It used XAML
  transforms, which is what leaked.
- `a157652` restores that architecture with composition motion and the Spicy CSS
  rest values. It is the current experiment on `perf-test`.
- User memory report for `a157652`: load spike **365 MB**, settle **265 MB**, then
  bounded cycles to **310 MB** and back. Across several songs the floor stayed near
  **305 MB**; new lines did not add memory.

## Architecture timeline

| Commit | What it did |
|---|---|
| `d11c840` | First translation of the Spicy CSS motion into the Expressive renderer. |
| `a76e26d`, `83e2c76`, `b9cc34d` | Replaced the copy/halo glow with a Composition `DropShadow` + alpha mask. |
| `5d9f017`, `024baff`, `c6fdd45`, `a729063` | Static alpha-mask twin work, mask reuse, glow lifecycle. |
| `435686e` | Checkpoint: lyrics disk cache and scroll takeover fixes. **All lines were built word-level up front.** |
| `1ff3b74` | Diagnostics counters. |
| `9dc6f37` | **Composition motion** for words, letters, rise and swell. |
| `21bbaae` | Rest transforms, center tracking, mask collection, progress opacity. |
| `7dbc903` | Far-row rest size, previous-song collection, fast activation fade. |
| `155c54b` | Whole-row rest scale (later removed as a workaround). |
| `fc27546` | Group-centre syllable origins (later reverted; CSS uses seams). |
| `23bb9f1` | Removed the second letter rest scale (reverted for the all-rows experiment, then re-applied). |
| `1480f6b` | Line-timed rows scaled from BuildRows. |
| `e8924b4` | First-frame spacing compensation via precomputed placements. |
| `260f3f0` | Margin compensation instead of row scale. |
| `784d373` | Added the "Compensate word spacing" user setting. |
| `3148eb3` | Static 0.95 gap when compensation is off. |
| `d5c1c84` | Aligned split letter blocks to the shaped word width. |
| `a157652` | **All-rows experiment**: every rich-synced line word-level from load, composition motion, CSS rest values. |

`custom` remains at `260f3f0`. `perf-test` is at `a157652`.

### Related session context

The local OpenCode session database was checked for the earlier work:

- `ses_e21e82e92ffevTN5AUOkJPrke2` — *WinUI 3 expressive lyric animations*:
  original CSS analysis, the first Expressive implementation, the
  `DropShadow` glow migration, hover/edge crop fixes, and
  `docs/lyrics-expressive.md`.
- `ses_e1ec72c23ffezM9LF4Xj6OaE2K` — *Lyrics caching and scroll behavior fixes*:
  lyrics disk cache, scroll takeover, mask reuse experiments, the memory
  regression that followed them, and the decision to keep only the scroll/cache
  fixes.
- `ses_e23283b05ffe6mO5DcA3PMumO9` — *Larger, bolder lyrics with adjustable size*:
  lyric sizing, panel width, hover-box radius and edge-padding work.
- `ses_e1e2ed91fffeizaqwgYi15GXCD` — this investigation.

Those sessions established the Expressive visual baseline and the earlier glow
lifecycle; this document focuses on what changed once memory and the
window/placeholder model were introduced.

## The Spicy CSS is the visual source of truth

The CSS says, and `d11c840` originally implemented:

### Lines

- Font: SF Pro Display/Text/Inter, weight 700, letter-spacing 0.
- Line height 1.2.
- No line-level size zoom. Line emphasis is opacity and blur only.
- Upcoming line opacity 0.10 in Koda's Expressive default (the CSS shows 0.76); sung line opacity 0.96; transition 0.6 s.
- Blur 1.1 px per distance level (not implemented in Windows).

### Words

- Rest: scale **0.95**, translate Y **0.01em**.
- Peak: scale **1.0505**, translate Y **-0.034em**.
- Settle back to **1 / 0**.
- Every unsung word in **every line** rests at 0.95 from the start.
- Extra word gap: **0.06em** on top of the font's normal space.
- Split syllables use seam origins:
  - first syllable: right edge, push `-0.02em`;
  - last syllable: left edge, push `+0.02em`;
  - middle: centre, no push.

### Held syllables

- Split only for words lasting at least 700 ms with 2-8 safe glyphs.
- Letters rest at their word-relative size (**1.0 scale / 0 Y**); the word
  supplies the 0.95 rest, so the composed resting scale is just 0.95 instead of
  0.95 * 0.95.
- Letter wave peaks around 1.12-1.175 with **0.03em** spread.
- The letter glow follows the wave.
- Letters spring back to **1 / 0** after the syllable ends.

### Why this matters for Windows

Spicy is a **word/letter layout from the start**. A one-block placeholder cannot
reproduce:

- per-word 0.95 transforms,
- per-letter 0.95 transforms,
- seam origins,
- the extra 0.06em gaps,
- the shaped width of the word.

Compensating a placeholder after the fact is why the later fixes kept fighting
each other.

## Memory investigation

### Symptoms

- Private memory started around 250 MB, climbed to 800 MB-1.1 GB during playback,
  then dropped back to ~250 MB.
- GPU shared/committed peaked around 738/708 MB in one manual session.
- A 470 MB dump showed ~157 MB heap, ~392 MB images/native text surfaces.
- Forced GC did not release the bulk; a natural gen2 eventually did.

### A/B controls

| Run | Treatment | Peak private |
|---|---|---:|
| `auto-quick2-20261116` | default | 901 MB |
| `auto-noglow-20261116` | no glow host/masks/shadows | 1,158 MB |
| `auto-nolyrics-20261116` | lyrics panel closed but still built | 730 MB |
| `auto-npmaskoff-20261116` | page fade mask off | 771 MB |
| `auto-nolyricsbuild-20261116` | lyrics content construction off | **258 MB** |

The peak was in lyrics content, not the page/cover/glow.

### Root cause

The per-frame `CompositeTransform` on every word and letter caused XAML to
re-rasterize text. Isolation:

| Run | Treatment | Peak |
|---|---|---:|
| `auto-static-nomotion-20261116` | no motion, no fill, no glow | 249 MB |
| `auto-static-motiononly-20261116` | motion only, XAML transform | 842 MB |
| `auto-staticfill-play-20261116` | static fill, XAML transform | 977 MB |
| `auto-pool-play-20261116` | default look, pooled words, XAML transform | 1,126 MB |

No motion stayed flat. Motion alone reproduced the growth. Reusing word elements
did not help.

### Composition motion fix

`9dc6f37` moved word bounce, letter wave and Sweep rise/swell to:
`Visual.Scale`, `Visual.CenterPoint`, and the `Translation` visual property with
`SetIsTranslationEnabled`.

| Run | Peak |
|---|---:|
| `auto-comp-motiononly-20261116` | 244 MB |
| `auto-comp-play-20261116` | 344 MB |
| `auto-comp-seek-20261116` | 253 MB |
| `auto-comp-skip-20261116` | 406 MB |
| `auto-comp-full-20261116` | 377 MB |
| `auto-comp-final-test-20261116` | 262 MB |

Motion-only became completely flat.

### Remaining first-song growth and masks

After virtualization was removed from the equation, the first song still rose
until a natural collection. The cause was **disposed glow masks/twins waiting for
GC**, plus the first word pool warm-up.

- `DisposeMask` disposes the mask, then `ReplaceMaskTwin` creates a new TextBlock
  because `GetAlphaMask` may return the same, now-disposed brush.
- Disposing masks/native surfaces does not free memory until they are collected.
- The live diagnostics showed masks created/disposed but no forced collection.

Fixes:

- `CollectDisposedMasks` now triggers a background collection after 30 disposed
  masks (`21bbaae`).
- `Load` collects **after** clearing the old rows/words (`7dbc903`), not before.
- A 5-minute first-song run then stayed flat at **212.9-230.1 MB**, with 175 masks
  created, 152 disposed and 8 forced collections.
- Lowering the threshold to 12 did not materially improve the peak and runs more
  GCs; 30 was kept.

### All-rows experiment (`a157652`)

`435686e` built all lines word-level from `BuildRows`; the later window reduced
memory at the cost of visual consistency. `a157652` restores the all-rows model
with composition motion.

User measurements on the experiment:

```
load spike:      365 MB
settle:          265 MB
cycles:          265 -> 310 -> 265 MB
floor across songs: ~305 MB
```

No cumulative growth across songs. The sawtooth is glow-mask/twin churn, bounded.

## Visual regression log

### Placeholder vs promoted row

Pixel-perfect before/after crops (`The adrenaline rush, oh-oh`):

```
before: 470 x 33 px
after:  494 x 33 px
```

Height identical; width +24 px. The extra width was inside `adrenaline`,
which is split into `a` + `drena` + `line`; `drena` becomes letter TextBlocks.
Separate letter blocks lose the shaped word's kerning/advances.

`d5c1c84` measures the shaped word and distributes the difference as static gaps
between letter blocks. This fixes the width, but it does not make a placeholder
equal to the true word/letter layout.

### Double letter scale

At rest Spicy composes word 0.95 and letter 0.95. Koda briefly changed letters to
1.0 to avoid a second shrink, which is not Spicy. `a157652` restores 0.95/0.02.

### Group-centre vs seam origins

`fc27546` changed joined syllables to scale around the whole word centre to stop
them pulling toward the seam. The CSS explicitly uses seam origins; the correct
fix is to keep the complete word/letter layout, not to invent a different origin.
`a157652` restores seams.

### Row scale and activation snap

Trying to match a placeholder's spacing by scaling the whole row caused the line
to grow when promoted. Frames 33/34 of one recording showed `Blood is in the water
now` widening in one frame while its height stayed constant. Animating the row
scale only softened the snap; it did not remove the architecture problem.

### Spacing compensation

`260f3f0` attempts to keep gaps proportional by translating words. That pushes
neighbours, which the user rejected. Compensation is now a setting; off restores
fixed layout gaps. `3148eb3` also shrinks the layout gap to 0.95 when compensation
is off, so the rich static row matches the 0.95 placeholder.

This helps the windowed path but is still a workaround. Spicy has no compensation
because it has no placeholder.

## Android / Spicy comparison

The Android `SyncedLyricsView.kt` is useful for architecture:

- `EXPRESSIVE_SPLIT_LETTERS = false`; the Expressive renderer works on syllable
  units, not letter TextBlocks.
- Future lines already use the karaoke renderer (`useKaraoke` is true for all
  future rows), so a line never swaps renderer on its turn.
- It uses shaped `TextLayoutResult` caches, not a TextBlock per letter.
- It uses variable font weight/width/size for Expressive.

But the user's instruction is to use **only the Spicy style** from Android, not the
other renderers. The CSS above remains authoritative for values. Do not copy the
Android Expressive size/weight table into the Spicy look.

## Current state

- `perf-test` at `a157652`:
  - every rich-synced line word/letter-level from load;
  - composition motion;
  - CSS rest values (word 0.95/0.01em, letters at word-relative 1.0/0, seam origins);
  - normal space + 0.06em word gap;
  - no spacing compensation in this mode;
  - `KODA_NP_WINDOWED=1` restores the previous windowed/placeholder path.
- `custom` at `260f3f0`: margin compensation and the windowed path. Row-scale
  experiments exist in its history but are no longer used in the final code.
- Settings:
  - `Compensate word spacing` toggles runtime word translations.
  - `Animate words`, `LyricsMotion`.
- Diagnostics:
  - `KODA_LYRICS_LIVE_LOG=1` logs masks, glows, pools, private MB, growth.
  - `KODA_LYRICS_GC_PROBE=1` + `%TEMP%\koda-lyrics-gc-request` runs a collection
    and logs before/after.
  - `KODA_GLOW_TEST` modes remain for glow experiments.
  - `KODA_NP_WINDOWED=1` all-rows experiment fallback.

## Open questions / next work

1. **Glyph mask cache.** Reusing one twin/mask per glyph+size+style could flatten
   the 265-310 MB sawtooth, but it changes mask ownership from the letter to the
   cache. Needs:
   - cache-owned, ref-counted masks;
   - keys for glyph, font size, weight/soft, family;
   - eviction on Load/resize/font change;
   - fallback for uncached glyphs/emoji.
   Do not do it while the current floor is stable unless the sawtooth costs frames.
2. **Font mismatch.** Koda uses Segoe UI Variable Text Bold; Spicy uses SF Pro
   Display/Text or Inter at 700. Koda's letterforms and widths differ.
3. **Line blur.** The CSS 1.1 px per distance is still missing. Real blur needs
   Win2D/Composition effects; see `lyrics-expressive.md`.
4. **Letter splitting.** The CSS splits held syllables into letters, but separate
   TextBlocks lose kerning. `AlignLetters` compensates total width; a shaped-text
   renderer (or a per-word overlay during static states) would be more faithful.
5. **All-rows memory.** Building every row is the visually correct architecture,
   but it has a high one-time cost (365 MB load spike vs ~265 MB live). If this is
   too high, optimize with a shared shaped-layout cache rather than reintroducing
   placeholders.

## Reproduction and evidence

Profiling kit: `C:\koda-profiles\kit`

- `Start-KodaProfile.ps1 -Mode play|seek|skip|full|test -Restart -RunDir ...`
- `sample-fast.ps1` - 100 ms private/WS/GPU samples.
- `analyze-koda-profile.py` - summary/SVG analysis.
- `wpr-capture.ps1` - elevated WPR capture.

Important run folders:

- `C:\koda-profiles\auto-static-nomotion-20261116` - flat control, 249 MB.
- `auto-static-motiononly-20261116` - XAML motion, 842 MB.
- `auto-staticfill-play-20261116` - XAML static fill, 977 MB.
- `auto-comp-motiononly-20261116` - composition motion, 244 MB.
- `auto-comp-play-20261116` - composition default, 344 MB.
- `auto-comp-full-20261116` - full scenario, 377 MB.
- `firstsong-probe-20261117` - mask-collection first song, 213-230 MB.
- `C:\koda-profiles\AUTOMATED-PROFILE-RESULTS.md` - consolidated tables.

## Lessons

- A frame-by-frame memory leak can be hidden from the managed heap; native text
  surfaces are the thing to watch.
- Do not diagnose XAML UI memory with GC counters alone. The live mask/glow
  counters and a private-bytes sampler were what isolated it.
- Reusing elements does not help if the per-frame transform is the allocator.
- A placeholder is a second renderer. If it does not match the rich renderer
  exactly, users will see the swap; matching it by scaling/compensating creates a
  chain of visual workarounds.
- The simplest correct visual model is the original one: every rich-synced line
  is word/letter-level from the start, with composition motion and the Spicy rest
  values. Memory should be solved by caching/collection, not by changing the
  rendering model.

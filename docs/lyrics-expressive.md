# The Expressive lyrics look

The Expressive preset is now the Spicy-inspired look translated from
`spicy.css` (Better Lyrics). It is implemented with native WinUI 3 transforms,
brushes and composition animations; there is no web view or extra rendering
package.

## What it does

### Word bounce

Every word-timed word is a pure function of the song clock, so pause, seek and
replay need no storyboard or animation object.

| Value | Amount |
|---|---|
| Rest | `scale 0.95`, `translateY 0.01em` |
| Peak | `scale 1.0505`, `translateY -0.034em` |
| Rise | `max(280ms, wordDuration + 60ms)` |
| Scale rise | lift rise + `90ms` |
| Lift easing | `cubic-bezier(0.33, 0, 0.58, 1)` |
| Scale easing | `cubic-bezier(0.37, 0, 0.63, 1)` |
| Lift settle | `1.64s`, `1.45 Hz`, damping `0.40` |
| Scale settle | `1.64s`, `0.88 Hz`, damping `0.64` |

The settle is an analytic damped spring, not the CSS keyframe table. The
formula reproduces Spicy's sampled keyframes to within a fraction of a percent
and does not need 42 stored frames.

### Syllables

A provider word split into syllables is kept together by `WordWrapPanel`.
Each syllable bounces from its inner seam:

- first syllable: transform origin at its right edge, push `-0.02em`;
- last syllable: origin at its left edge, push `+0.02em`;
- middle syllables: centre, no push.

That keeps a split word visually attached while it bounces.

### Held syllables and the letter wave

A word is a held syllable when it lasts at least `700ms`. Only 2–8 safe glyphs
are split into letters; longer words and complex scripts keep the word-level
bounce.

Each letter:

- rests at the word's own size (`scale 1.0`, no extra offset), so the word's
  `0.95` rest is not applied a second time;
- gets `duration / letterCount` of the highlight;
- waves for `slot * 2 + 0.55s`;
- starts at `slot * (i + 1) / 1.2 + duration / 6 - waveDuration * 0.46`;
- peaks at `scale ≈ 1.121`, lifts vertically, and spreads outward by
  `0.03em × (i - centre)`;
- glows through a Composition `DropShadow` while its wave is at its peak;
- springs back to normal with the scale spring after the syllable ends.

The wave table is copied from the CSS (every 4% from 4% to 92%), so the motion
matches the source.

### Highlight and colors

- Bright edge: `-20% → 100%` of the word or letter.
- Soft edge: `20%` behind the bright edge.
- Unsung: `50%` white (`#80FFFFFF`).
- Sung: `85%` white (`#D9FFFFFF`).
- Hovered active line: up to `100%` white.
- Upcoming line opacity: `0.10`; sung line opacity: `0.96`; active: `1.0`.

### Lines and extras

- The line under the pointer gets a `10%` white rounded box that grows from
  `scale 0.94` to `1` with a composition animation.
- Interlude dots breathe `0.92 → 1.08` on a `2.4s` cycle.
- The Expressive renderer keeps painting a line for `2.6s` after its last word
  so the springs finish instead of freezing.

## Where it lives

| Piece | File |
|---|---|
| Motion math, springs, safe letter splitting | `src/Koda.Core/Lyrics/LyricsMotion.cs` |
| Word/letter views, painting, hover box, dots | `src/Koda/Pages/LyricsPanel.cs` |
| Preset values and the saved-preset refresh | `src/Koda.Core/Settings.cs` |
| Motion tests | `tests/Koda.Core.Tests/LyricsMotionTests.cs` |

## What is approximated

- **Glow** — no longer approximated. The Expressive letter glow uses a
  Composition `DropShadow` with the letter's alpha mask, a transparent sprite
  behind the text, and `BlurRadius`/`Opacity` driven from the same
  `LyricsMotion.Letter` wave factor. This is a true Gaussian glow.
- **Line blur** — still omitted. The CSS uses `1.1px` per distance level; WinUI
  would need a Win2D or custom text-rendering path. Opacity still separates the
  active, sung and upcoming lines.

## Composition DropShadow glow

### API checks

On the Windows App SDK 2.3.9 surface this project references:

- `TextBlock.GetAlphaMask()` exists and returns a `CompositionBrush`.
- `DropShadow.Mask`, `BlurRadius`, `Opacity`, `Color` and `Offset` exist, and
  `BlurRadius`/`Opacity` are marked animatable.
- `SpriteVisual.Shadow` exists.
- `ElementCompositionPreview.SetElementChildVisual` adds the sprite as the last
  child of the host's visual tree.

### Host choice

`SetElementChildVisual` draws **on top of the host element's content**, not
behind the text. The implementation therefore puts an empty `Grid` before the
sharp `TextBlock` in the letter's `Grid`:

```text
Grid (letter)
  Grid (empty glow host)   <- sprite + DropShadow live here
  TextBlock (sharp glyph)
```

The sprite is a child of the empty layer; the sharp glyph is a sibling drawn
after it, so the glow never washes over the sharp text. `Offset` stays at
`Vector3.Zero`. This was chosen over hosting the sprite on the text itself
because the text-host version draws the shadow over the glyph.

### Lifecycle

- The shadow and sprite are created lazily only when the letter's wave glow is
  above `0.01`.
- The alpha mask comes from `letter.Text.GetAlphaMask()`.
- The mask and sprite size are refreshed on `TextBlock.SizeChanged` and after
  `ReshapeWord` (font, size, DPI or theme change).
- The sprite is released when the wave glow returns to `0`; the empty glow host
  remains so the letter's layout does not change.
- `LyricsGlowIntensity` is still the multiplier; zero creates no glow host or
  shadow at all.

### Driving the glow

The existing wave is a pure function of the song clock, not a storyboard.
`SetLetterMotion` therefore sets the shadow's `Opacity` and `BlurRadius` from
the same `LetterMotion.Glow` value each frame:

```csharp
shadow.Opacity = (float)Math.Clamp(glow, 0, 1);
shadow.BlurRadius = (float)(4 + 12 * Math.Clamp(glow, 0, 1));
```

There are no composition animations to start, stop or seek.

### Light and dark surfaces

The glow has its own colour, `_glowColor`, instead of borrowing the text's. A
`DropShadow` can only darken what sits behind it, so the colour that reads as
light on the player would read as a smear in the light drawer:

| Surface | Colour | Strength |
|---|---|---|
| Now Playing (always dark) | `#FFFFFF` | full range (`_glowMax = 2`; opacity still clamps at 1) |
| Inline drawer and mini (light theme) | `#595959` | capped at `0.4` (`_glowMax`) |

The cap also shortens the reach, because `BlurRadius` is derived from the same
strength (`4 + 12 × opacity`). `_glow` still gates the swell and every letter's
wave is applied before the glow block in `SetLetterMotion`; `_glowMax` only limits
the halo, so a capped or disabled glow never removes the motion.

A white "paper bloom" was tried first: keep the glow white in both themes so it
lifts the `SolidBackgroundFillColorBase` surface instead of inking it. On the
near-white drawer it was not visible, so the grey cap above is the current state.

**Open decision:** disable the halo in light mode rather than tinting it.
Setting `_glowMax = 0` in the light branch of `ReadPalette` does that without
touching the motion, and no shadow or mask is built because the strength never
crosses the `0.01` gate in `SetLetterMotion`.

### Measured cost

Capture: City Walls, ten-second segment, the old halo baseline and the new
DropShadow path playing from the same 2:11 seek, x64 Debug, `winapp perf`
process-resource samples. The tool does not record GPU counters and composition
scopes are app-side elapsed API time, not displayed FPS.

| Mode | Process CPU | Composition calls | Private bytes (max) | Working set (max) |
|---|---:|---:|---:|---:|
| Halo | 5,578 ms / 9.4 s | 20,105 | 306 MB | 424 MB |
| DropShadow | 4,172 ms / 9.4 s | 9,230 | 312 MB | 439 MB |

The DropShadow path used about **25% less process CPU** and **54% fewer
composition calls**. Memory was not better in this run (about 6 MB more
private memory and 15 MB more working set); the one-second sampling is coarse
and this needs a longer run to call. GPU attribution and presented-FPS numbers
are not available from this capture.

The visual check showed a soft Gaussian bloom around the held "are" at 1:28
with no rectangular box and no glow over the sharp text. A halo recording at
the identical clock time was not captured, so the before/after comparison still
needs a controlled replay of the same segment.

## Alternative: true blur and glow with Win2D

If pixel-perfect Spicy glow and per-line blur become a hard requirement, the
only practical route is to render the affected text through Direct2D with
Win2D. This is still native, but it is a second text pipeline, not a property
on the existing `TextBlock`s.

### Package

Add:

```xml
<PackageReference Include="Microsoft.Graphics.Win2D" Version="..." />
```

Use a Win2D version built for Windows App SDK / WinUI 3. `Microsoft.Graphics.Win2D`
is the WinUI 3 flavor; the older UWP package is not appropriate.

### Option A: composition surface + blur effect

1. Draw the line, or each letter, into a `CanvasRenderTarget` with
   `CanvasDrawingSession.DrawText` and the same `CanvasTextFormat` (family,
   size, weight, tracking, line height).
2. Create a `GaussianBlurEffect` from
   `Microsoft.Graphics.Canvas.Effects.GaussianBlurEffect` with
   `BlurAmount = 1.1f * distanceLevel` for the line, or `4–16px` for a letter
   glow.
3. Wrap it in a `CompositionEffectBrush` through
   `Compositor.CreateEffectFactory`, and display it with a `SpriteVisual`
   behind (or in place of) the sharp text.
4. For the line blur, capture the row's visual with `CompositionVisualSurface`,
   blur the surface, and draw it in a layer behind the sharp row. For the glow,
   draw the letter once sharp and once blurred behind it.

Sketch:

```csharp
var compositor = ElementCompositionPreview.GetElementVisual(row.Host).Compositor;

var surface = compositor.CreateVisualSurface();
surface.SourceVisual = ElementCompositionPreview.GetElementVisual(row.Host);
surface.SourceSize = new Vector2((float)row.Host.ActualWidth, (float)row.Host.ActualHeight);

var source = compositor.CreateSurfaceBrush(surface);
var blur = new GaussianBlurEffect
{
    BlurAmount = 1.1f * level,
    Source = new CompositionEffectSourceParameter("source"),
};
var factory = compositor.CreateEffectFactory(blur, new[] { "BlurAmount" });
var brush = factory.CreateBrush();
brush.SetSourceParameter("source", source);

var sprite = compositor.CreateSpriteVisual();
sprite.Brush = brush;
sprite.Size = new Vector2((float)row.Host.ActualWidth, (float)row.Host.ActualHeight);
```

Caveats:

- `SetElementChildVisual` draws on top of the element, not behind it. A true
  behind-the-text blur needs a separate layer or a Win2D-drawn replacement for
  the whole row.
- The captured row must be re-captured on size, scroll, font, theme and DPI
  changes.
- The blurred layer must be clipped to the lyrics panel and kept in sync with
  the reading anchor.

### Option B: a Win2D lyrics surface

Replace the XAML text in the Expressive renderer with a `CanvasControl` or
`SwapChainPanel` that draws every visible line:

- `CanvasTextLayout` for shaping and per-word measurement;
- a blurred copy for line-distance blur;
- a blurred copy per glowing letter;
- the sharp text on top;
- a manual hit-test map for line taps and hover.

This gives exact CSS fidelity, but it also means:

- reimplementing line wrapping, right-aligned duet lines, backing vocals,
  instrumental dots and the hover box;
- keeping selection/accessibility out of scope or adding a parallel XAML tree
  for automation;
- syncing the song clock, scrolling and the panel's fade;
- a larger GPU/CPU budget and a new dependency.

### Recommendation

`DropShadow` is now the Expressive letter glow: it is a real Gaussian, removes
the layered copies, and measured cheaper on CPU and composition calls. Keep the
alpha-mask refresh and lazy lifecycle in place. Only per-line blur still
requires Win2D.

## Known limits

- Letter splitting loses kerning and ligatures; it is restricted to
  Latin/Greek/Cyrillic glyphs, with no combining marks, emoji, Arabic, Indic,
  CJK or Thai.
- Word-level bounce still applies to complex scripts.
- The Gaussian `DropShadow` glow covers the letter views. Word-level glow was
  not part of the previous halo implementation either, so it is still a
  follow-up.
- A light surface has no light to add, so the light theme's glow is a faint grey
  halo rather than a bloom. Whether to remove it there is still open; see
  [Light and dark surfaces](#light-and-dark-surfaces).
- Per-line `filter: blur()` is not implemented and still needs Win2D or a
  custom text pipeline.
- The performance sample is one ten-second run. GPU attribution and presented
  FPS were not captured.
- The old `Expressive` preset values are refreshed when the saved preset is
  still Expressive; a `Custom` mix is left alone.

## Verification

- `dotnet test tests/Koda.Core.Tests/Koda.Core.Tests.csproj`
- `dotnet build src/Koda/Koda.csproj -p:Platform=x64`
- Play a rich-synced song with held syllables; check word bounce, syllable
  seams, letter wave, pause/resume, forward and backward seek, hover, backing
  vocals, narrow windows, `LyricsMotion` off and `LyricsGlowIntensity` 0.
- Check the Gaussian glow follows the glyphs, survives pause/seek, and
  disappears with `LyricsGlowIntensity` 0.

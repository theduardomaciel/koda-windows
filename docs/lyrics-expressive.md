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

- rests at `scale 0.95`, `translateY 0.02em`;
- gets `duration / letterCount` of the highlight;
- waves for `slot * 2 + 0.55s`;
- starts at `slot * (i + 1) / 1.2 + duration / 6 - waveDuration * 0.46`;
- peaks at `scale ≈ 1.121`, lifts vertically, and spreads outward by
  `0.03em × (i - centre)`;
- glows through two halo layers while its wave is at its peak;
- springs back to normal with the scale spring after the syllable ends.

The wave table is copied from the CSS (every 4% from 4% to 92%), so the motion
matches the source.

### Highlight and colors

- Bright edge: `-20% → 100%` of the word or letter.
- Soft edge: `20%` behind the bright edge.
- Unsung: `50%` white (`#80FFFFFF`).
- Sung: `85%` white (`#D9FFFFFF`).
- Hovered active line: up to `100%` white.
- Upcoming line opacity: `0.76`; sung line opacity: `0.96`; active: `1.0`.

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
| Settings preview | `src/Koda/Controls/LyricsPreview.cs` |
| Motion tests | `tests/Koda.Core.Tests/LyricsMotionTests.cs` |

## What is approximated

WinUI 3 has no XAML text-shadow and no `UIElement.Effect`; the referenced
packages do not include a Gaussian blur effect. The native implementation
therefore does this:

- **Glow** — two scaled copies of each letter sit behind the sharp letter at low
  opacity. This reads as a halo, but it is not a real Gaussian shadow.
- **Line blur** — omitted. The CSS uses `1.1px` per distance level; WinUI would
  need a custom text-rendering path to reproduce it. Opacity still separates
  the active, sung and upcoming lines.

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

Keep the current XAML implementation as the default. The word bounce, syllable
seams, letter wave, highlight, line dimming, hover box and dots are native and
account for most of the look. Only add the Win2D path if a true soft glow and
per-line blur are required, and make it an opt-in fidelity mode so the native
path stays the fallback.

## Known limits

- Letter splitting loses kerning and ligatures; it is restricted to
  Latin/Greek/Cyrillic glyphs, with no combining marks, emoji, Arabic, Indic,
  CJK or Thai.
- Word-level bounce still applies to complex scripts.
- Text glow is a halo of scaled copies, not a Gaussian blur.
- Per-line `filter: blur()` is not implemented.
- The old `Expressive` preset values are refreshed when the saved preset is
  still Expressive; a `Custom` mix is left alone.

## Verification

- `dotnet test tests/Koda.Core.Tests/Koda.Core.Tests.csproj`
- `dotnet build src/Koda/Koda.csproj -p:Platform=x64`
- Play a rich-synced song with held syllables; check word bounce, syllable
  seams, letter wave, pause/resume, forward and backward seek, hover, backing
  vocals, narrow windows, `LyricsMotion` off and `LyricsGlowIntensity` 0.

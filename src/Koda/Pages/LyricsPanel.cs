using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Koda.Controls;
using Koda.Core;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Koda.Pages;

/// <summary>
/// The lyrics panel: the lines, the one being sung lit, and the words inside it filling
/// from their left edge as they are sung.
///
/// The motion is the Mac's, word for word. A word fills through a soft-edged mask that
/// runs from its left edge, rises a touch as it starts (so a line moves as a ripple),
/// and a note held long swells and glows. The lines around the one being sung step
/// back: dimmer, a little smaller. The panel glides to the line being sung with a
/// spring - it lands a third of the way down, so the listener reads ahead.
///
/// The listener's own correction for a song (the offset the Library keeps) is honoured,
/// and the audio leads the lyrics by a fraction of a second, so a line lights as it is
/// sung rather than after.
/// </summary>
internal sealed class LyricsPanel : IDisposable
{
    /// <summary>The audio leads the lyrics: a line lights as it is sung, not after.</summary>
    private const double Lead = 0.15;

    /// <summary>Where the line being sung sits in the panel: a third of the way down, so the
    /// eye barely travels and the lines that follow are already in view.</summary>
    private const double ReadingAnchor = 0.33;

    /// <summary>How long the glide takes.</summary>
    private const int GlideMilliseconds = 550;

    /// <summary>A gap of this many seconds or more shows the interlude dots.</summary>
    private const double InterludeSeconds = 4.0;

    /// <summary>How long a word takes to rise, in seconds.</summary>
    private const double RiseSeconds = 0.65;

    /// <summary>How far a word rises, as a part of the text size: it keeps the lift visible
    /// when the words are large.</summary>
    private const double RiseRatio = 0.09;

    /// <summary>A note held longer than this starts to swell.</summary>
    private const double SustainStart = 0.9;

    /// <summary>The soft front of the filling mask, as a part of the word's width.</summary>
    private const double Feather = 0.35;

    /// <summary>How far a swelling note grows.</summary>
    private const double SwellScale = 0.045;

    /// <summary>How wide the words sit from each other, as a part of the text size.</summary>
    private const double WordSpacing = 0.25;

    /// <summary>A little tracking, so the lines breathe rather than read thin, as the Mac's do.</summary>
    private const int Tracking = 20;

    /// <summary>The line height, as a part of the text size: Apple's lyrics are airy.</summary>
    private const double LineHeightFactor = 1.35;

    /// <summary>The Text optical size of Segoe UI Variable: wider letterforms at reading sizes
    /// than the Display cut, which is what made the lyrics look thin.</summary>
    private static readonly FontFamily LyricFont = new("Segoe UI Variable Text");

    /// <summary>The two ends of a word's fill: the sung white and the waiting grey.</summary>
    private static readonly Color SungColor = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color UnsungColor = Color.FromArgb(0x61, 0xFF, 0xFF, 0xFF);

    /// <summary>Spicy's colors: 85% white once sung, 50% white while waiting.</summary>
    private static readonly Color ExpressiveSung = Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF);

    /// <summary>The static mask twin's fill: fully opaque white, the reference alpha for
    /// the mask; the shadow's own opacity still scales the visible glow.</summary>
    private static readonly Color MaskTwinColor = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color ExpressiveUnsung = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
    private static readonly Color ExpressiveHoverSung = Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF);
    private static readonly Color ExpressiveHoverUnsung = Color.FromArgb(0xBF, 0xFF, 0xFF, 0xFF);
    private static readonly Color ExpressiveActiveHoverSung = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color ExpressiveActiveHoverUnsung = Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF);
    private static readonly Color HoverBoxColor = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);

    /// <summary>How long after a line's end its springs may keep painting.</summary>
    private const double ExpressiveTailSeconds = 2.6;

    /// <summary>How far the hover box reaches past the text, as a part of the text size.</summary>
    private const double HoverBoxPad = 0.35;

    /// <summary>The hover box corner radius, as a part of the text size. It is clamped so a
    /// very small or very large lyric size still reads as a rounded box.</summary>
    private const double HoverBoxRadiusRatio = 0.3;
    private const double HoverBoxRadiusMin = 8;
    private const double HoverBoxRadiusMax = 20;

    /// <summary>The sung line opacity Spicy uses: a little below the active line.</summary>
    private const double ExpressiveSungLineOpacity = 0.96;

    /// <summary>The width the lyrics are designed for; narrower panels scale the words down.</summary>
    private const double DesignWidth = 596;

    /// <summary>The height the lyrics are designed for; shorter panels scale the words down.</summary>
    private const double DesignHeight = 900;

    /// <summary>How far the words may shrink in a narrow panel.</summary>
    private const double MinScale = 0.55;

    /// <summary>No size is fitted below this, however narrow the panel.</summary>
    private const double MinFontSize = 12;

    // The look, read from the settings when a song's lyrics load. A change in
    // Settings applies the next time the panel fills, not mid-song.
    private FontFamily _font = LyricFont;
    private double _lineHeightFactor = LineHeightFactor;
    private double _feather = Feather;
    private double _risePixels = 2.5;
    private double _baseFontSize = 36;
    private double _baseLineGap = 32;
    private double _fontSize = 36;
    private double _lineGap = 32;
    private BackingVocalsMode _backingMode = BackingVocalsMode.Shown;
    private double _idleNear = 0.5;
    private double _idleFar = 0.36;
    private bool _glow = true;
    private double _glowIntensity = 1;
    private bool _motion = true;
    private LyricsRenderer _renderer = LyricsRenderer.Sweep;
    private string _glowTest = "";
    private bool _glowBlurFixed;
    private bool _glowTransformFixed;
    private bool _glowNoMask;
    private bool _glowStaticMask;
    private double _maskTwinOpacity = 0.01;
    private string _maskHide = "";
    private int _paintedLetters;
    private readonly bool _frameLog = Environment.GetEnvironmentVariable("KODA_LYRICS_FRAME_LOG") == "1";
    private readonly bool _liveLog = Environment.GetEnvironmentVariable("KODA_LYRICS_LIVE_LOG") == "1";
    private readonly System.Diagnostics.Stopwatch _frameClock = System.Diagnostics.Stopwatch.StartNew();
    private long _lastFrameLogMs;
    private long _lastLiveLogMs;
    private long _lastLivePrivateBytes;

    private readonly ScrollViewer _scroll;
    private readonly StackPanel _lines;
    private readonly Border _topSpacer = new();
    private readonly Border _spacer = new();
    private readonly DispatcherQueueTimer _tween;
    private readonly DispatcherQueueTimer _frame;

    private readonly List<Row> _rows = [];
    private readonly List<LyricLine> _lyricLines = [];
    private readonly Queue<Letter> _glowWarmup = new();
    private readonly HashSet<Letter> _glowWarmupSet = [];
    private readonly List<Letter> _liveGlows = [];
    private readonly HashSet<Letter> _liveGlowSet = [];
    private readonly Dictionary<MaskKey, CompositionBrush> _glyphMasks = [];
    private readonly Queue<MaskKey> _glyphMaskOrder = new();
    private int _glowWindowIndex = -2;
    private CubicBezierEasingFunction? _hoverEase;
    private ScalarKeyFrameAnimation? _hoverOpacityIn;
    private ScalarKeyFrameAnimation? _hoverOpacityOut;
    private Vector3KeyFrameAnimation? _hoverScaleIn;
    private Vector3KeyFrameAnimation? _hoverScaleOut;
    private readonly bool _gcProbe = Environment.GetEnvironmentVariable("KODA_LYRICS_GC_PROBE") == "1";
    private StackPanel? _leadingDots;
    private List<Ellipse> _leadingDotItems = [];
    private Row? _current;
    private double _offset;
    private bool _userScrolled;
    private bool _loaded;
    private bool _visible;
    private bool _playing;
    private bool _needsGlide;
    private bool _needsFullPaint;
    private int _repaintFrom = -1;
    private int _repaintTo = -1;
    private double _position;
    private double _freezeAt = double.NaN;
    private double _panelWidth;
    private double _panelHeight;
    private bool _interludeActive;

    /// <summary>One word in the panel: the element that draws it and the timings behind it.
    /// A held syllable is drawn as a row of <see cref="Letter"/>s instead of one text block.</summary>
    private sealed class Word
    {
        public required LyricWord Data { get; init; }
        public required FrameworkElement Element { get; init; }
        /// <summary>The word's own text block, or null when it is split into letters.</summary>
        public TextBlock? Text { get; init; }
        /// <summary>The mask the word fills through, or null when each letter has its own.</summary>
        public LinearGradientBrush? Fill { get; init; }
        /// <summary>The bounce of the word.</summary>
        public required CompositeTransform Transform { get; init; }
        /// <summary>The letters of a held syllable; empty for an ordinary word.</summary>
        public List<Letter> Letters { get; } = [];
        /// <summary>Whether this is a syllable of the word before it: the two may not be
        /// split by a line break.</summary>
        public bool JoinsPrevious { get; init; }
        /// <summary>The size the word is drawn at, for converting em units to pixels.</summary>
        public double FontSize { get; set; }
        /// <summary>How far the syllable pushes outward at its seam, in ems.</summary>
        public double PushX { get; set; }
        /// <summary>The fill last written, so a word that is not changing is not redrawn.</summary>
        public double PaintedFill { get; set; } = -1;
        /// <summary>The lit edge last written, for the Expressive sweep.</summary>
        public double PaintedLit { get; set; } = double.NaN;
        /// <summary>The hover state last written.</summary>
        public bool PaintedHovered { get; set; }
        public bool PaintedActive { get; set; }
        /// <summary>The rise last written.</summary>
        public double PaintedRise { get; set; } = -1;
        /// <summary>The swell last written.</summary>
        public double PaintedSwell { get; set; } = -1;
        /// <summary>The Expressive bounce last written.</summary>
        public double PaintedScale { get; set; } = -1;
        public double PaintedX { get; set; } = double.NaN;
        public double PaintedY { get; set; } = double.NaN;
    }

    /// <summary>One letter of a held syllable: its own fill, wave and glow layers.</summary>
    private sealed class Letter
    {        public required FrameworkElement Element { get; init; }
        public required TextBlock Text { get; init; }
        /// <summary>Test only: the never-changing twin the alpha mask is taken from.</summary>
        public TextBlock? MaskText { get; set; }
        public required LinearGradientBrush Fill { get; init; }
        public required CompositeTransform Transform { get; init; }
        /// <summary>The empty layer behind the sharp letter that hosts the DropShadow sprite.</summary>
        public Grid? GlowHost { get; init; }
        /// <summary>The wave transform mirrored onto the glow host.</summary>
        public CompositeTransform? GlowTransform { get; init; }
        public DropShadow? Shadow { get; set; }
        public SpriteVisual? GlowVisual { get; set; }
        public CompositionBrush? Mask { get; set; }
        public CompositionBrush? GlowBrush { get; set; }
        public bool NeedsMaskRefresh { get; set; }
        public bool GlowWanted { get; set; }
        public int RowIndex { get; set; }
        public int Index { get; init; }
        public int Count { get; init; }
        public double PaintedLit { get; set; } = double.NaN;
        public bool PaintedHovered { get; set; }
        public bool PaintedActive { get; set; }
        public double PaintedScale { get; set; } = -1;
        public double PaintedX { get; set; } = double.NaN;
        public double PaintedY { get; set; } = double.NaN;
        public double PaintedGlow { get; set; } = -1;
        public double PaintedBlur { get; set; } = -1;
        /// <summary>True once this letter's glow has actually been visible.</summary>
        public bool GlowShown { get; set; }
        /// <summary>True once the glow ended and its surfaces were freed.</summary>
        public bool GlowDone { get; set; }
    }

    /// <summary>The identity of a static glyph mask: one brush can feed every letter
    /// with the same character and shape.</summary>
    private readonly record struct MaskKey(
        string Text,
        string Family,
        double Size,
        int Weight,
        int Spacing,
        double LineHeight);

    /// <summary>One line in the panel: its words, its backing vocals and the line itself.</summary>
    private sealed class Row
    {
        public required LyricLine Line { get; init; }
        /// <summary>The element that holds the line, for finding its place in the panel.</summary>
        public FrameworkElement Host { get; set; } = null!;
        public required Panel Words { get; init; }
        public required WordWrapPanel Background { get; init; }
        public List<Word> WordItems { get; } = [];
        public List<Word> BackgroundItems { get; } = [];
        /// <summary>The three dots of the gap that follows this line, when the gap is long.</summary>
        public List<Ellipse> Dots { get; } = [];
        /// <summary>The dots' container; always in the flow, faint when its gap is not playing.</summary>
        public StackPanel? DotsHost { get; set; }
        /// <summary>The rounded box that grows behind the line under the pointer.</summary>
        public Border? HoverBox { get; set; }
        /// <summary>Whether the pointer is over the line.</summary>
        public bool Hovered { get; set; }
        /// <summary>How opaque the row is: the one being sung is full, the rest step back.</summary>
        public double Opacity { get; set; } = 0.36;
    }

    public LyricsPanel(ScrollViewer scroll, StackPanel lines, DispatcherQueue queue)
    {
        _scroll = scroll;
        _lines = lines;

        // The glide: the panel moves to the line being sung, never jumps.
        _tween = queue.CreateTimer();
        _tween.Interval = TimeSpan.FromMilliseconds(16);
        _tween.Tick += OnTweenTick;

        // The words: they fill while they are sung, so the panel keeps a frame timer.
        // The position is read live, every frame: the app's own tick only comes
        // twice a second, and a word that fills in half-second steps reads as a
        // sequence of keyframes, not as a word being sung.
        _frame = queue.CreateTimer();
        _frame.Interval = TimeSpan.FromMilliseconds(16);
        _frame.Tick += (_, _) =>
        {
            if (_frameLog)
            {
                LogFrameGap();
            }

            if (_gcProbe)
            {
                CheckGcProbe();
            }

            if (PositionSource is { } source)
            {
                SetPosition(double.IsNaN(_freezeAt) ? source() : _freezeAt);
            }

            // Following the song here, not on the app's twice-a-second tick: the line
            // being sung changes the moment the song reaches it.
            Follow();

            if (_liveLog)
            {
                LogLiveSet();
            }
        };

        // The words are laid out in rows that fit the panel, so a line breaks where it should.
        _scroll.SizeChanged += (_, _) => RefreshLayout();
        // The pane collapse, a monitor move and a zoom change can all leave the scroll
        // viewer at a different size without a SizeChanged reaching us; every layout pass
        // checks, and does nothing when the size is the same.
        _scroll.LayoutUpdated += (_, _) => RefreshLayout();
    }

    /// <summary>Re-reads the panel's size and reflows. Called when the window moved to
    /// another display or zoom, where the ScrollViewer's own SizeChanged can be missed.</summary>
    public void RefreshLayout()
    {
        // A layout pass is the moment the ScrollViewer can report a stale slot after a
        // pane, display or zoom change, so this is the one place that pays for the
        // TransformToVisual probe.
        if (SyncSize(probe: true))
        {
            Paint();
        }
    }

    /// <summary>Reads the width and height the lyrics are actually given, not the last size
    /// an event reported, and reflows when they changed. A sidebar, a monitor or a zoom
    /// change can move the panel without any of those events reaching us. Returns whether
    /// it re-measured.</summary>
    private bool SyncSize(bool probe = false)
    {
        var width = _scroll.ActualWidth;
        var height = _scroll.ActualHeight;

        // The fast path runs every frame from Paint. Do not allocate a GeneralTransform
        // there; the layout pass above probes the real slot when it may have moved.
        if (!probe
            && Math.Abs(width - _panelWidth) < 0.5
            && Math.Abs(height - _panelHeight) < 0.5)
        {
            return false;
        }

        // The scroll viewer can still report the width of an old slot after a pane/display
        // change, and its content is then clipped by the window. The width that matters is
        // the room from the panel's left edge to the window's right edge.
        if (_scroll.XamlRoot is { } root)
        {
            try
            {
                var origin = _scroll.TransformToVisual(null)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
                var visible = root.Size.Width - origin.X - 8;
                if (visible > 0)
                {
                    width = Math.Min(width, visible);
                }

                var visibleHeight = root.Size.Height - origin.Y - 8;
                if (visibleHeight > 0)
                {
                    height = Math.Min(height, visibleHeight);
                }
            }
            catch (Exception)
            {
                // not in the tree yet
            }
        }

        if (width < 1)
        {
            return false; // collapsed: keep the size the panel had when it was on screen
        }

        var widthChanged = Math.Abs(width - _panelWidth) >= 0.5;
        var heightChanged = Math.Abs(height - _panelHeight) >= 0.5;
        if (!widthChanged && !heightChanged)
        {
            return false;
        }

        _panelWidth = width;
        _panelHeight = height;
        ApplyTextWidth();
        UpdateSpacers();

        var oldSize = _fontSize;
        UpdateScale();
        if (Math.Abs(_fontSize - oldSize) >= 0.25)
        {
            Reshape();
        }

        // Measure the new wrap/size before aiming the scroll at the line being sung.
        _scroll.UpdateLayout();

        // A display, pane or zoom change is a fresh frame: put the sung line back on the
        // reading anchor at once, even if the listener had scrolled away before the change.
        if (_current is not null)
        {
            JumpTo(_current);
        }

        return true;
    }

    /// <summary>A line's right-click menu asked to share it: the lines, and the one it was opened on.</summary>
    public Action<IReadOnlyList<LyricLine>, int>? Share { get; set; }

    /// <summary>The timed lines on show, for sharing from elsewhere (the player's More menu).</summary>
    public IReadOnlyList<LyricLine> Lines => _lyricLines;

    /// <summary>The line being sung, or the first one before it starts.</summary>
    public int CurrentIndex => Math.Max(0, Lyrics.IndexOf(_position, _lyricLines) ?? 0);

    /// <summary>Whether the line being sung sits above the middle of the panel: the follow
    /// pill's chevron points at it, so the listener knows which way the words are.</summary>
    public bool CurrentLineIsAbove
    {
        get
        {
            if (_current is null || _scroll.ViewportHeight <= 1)
            {
                return true;
            }

            try
            {
                var top = _current.Host.TransformToVisual(_scroll)
                    .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                // Past the reading anchor means the listener scrolled down, so the line is
                // above where the panel keeps it; before it, the line is below.
                return top < _scroll.ViewportHeight * ReadingAnchor - _current.Host.ActualHeight / 2;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    /// <summary>Called when a line is tapped: the song jumps to that line.</summary>
    public Action<double>? Seek { get; set; }

    /// <summary>Where the song is, read live: the panel asks every frame.</summary>
    public Func<double>? PositionSource { get; set; }

    /// <summary>The listener's correction for this song, in seconds.</summary>
    public double Offset
    {
        get => _offset;
        set
        {
            if (Math.Abs(_offset - value) < 0.0001)
            {
                return;
            }

            _offset = value;
            _needsFullPaint = true;
        }
    }

    /// <summary>Whether the listener has scrolled away; the panel stops following then.</summary>
    public bool UserScrolled
    {
        get => _userScrolled;
        set
        {
            if (_userScrolled == value)
            {
                return;
            }

            _userScrolled = value;
            ScrolledAwayChanged?.Invoke(value);
        }
    }

    /// <summary>Told when the listener scrolls away from the line being sung, or back to it.</summary>
    public Action<bool>? ScrolledAwayChanged { get; set; }

    /// <summary>Hands the panel back to the song: the line being sung glides into place.</summary>
    public void ResumeFollowing()
    {
        UserScrolled = false;
        if (_current is not null)
        {
            GlideTo(_current);
        }
    }

    /// <summary>The song's place in time, with the listener's correction and the lead.</summary>
    public void SetPosition(double positionSeconds)
    {
        var next = positionSeconds + Lead - _offset;
        // A jump means a seek or a replay: the lines behind and ahead must be repainted,
        // or the line that was partly sung keeps its half-filled words. Only the span
        // between the old and new lines can change state; repainting the whole song on
        // every seek touched hundreds of text surfaces the panel never shows.
        if (next < _position - 0.15 || next > _position + 0.6)
        {
            var oldIndex = _lyricLines.Count > 0
                ? Lyrics.IndexOf(_position, _lyricLines) ?? -1
                : -1;
            var newIndex = _lyricLines.Count > 0
                ? Lyrics.IndexOf(next, _lyricLines) ?? -1
                : -1;
            SetRepaintRange(oldIndex, newIndex);

            // A backward jump is a seek/replay. Re-arm the target window instead of
            // disposing every glow: the ones still in the window can be reused.
            if (next < _position - 0.15)
            {
                RearmGlows(newIndex);
            }
        }

        _position = next;
    }

    /// <summary>The appearance settings, read once per fill so a song never changes look
    /// halfway through.</summary>
    private void ReadLook()
    {
        var settings = Koda.Core.Settings.Current;
        _font = string.IsNullOrWhiteSpace(settings.LyricsFont) ? LyricFont : new FontFamily(settings.LyricsFont);
        _lineHeightFactor = LineHeightFactor * settings.LyricsLineSpacing;
        _feather = 0.08 + 0.55 * settings.LyricsSoftness;
        _baseFontSize = settings.LyricsFontSize;
        _baseLineGap = settings.LyricsLineGap;
        _backingMode = settings.BackingVocals;
        _idleFar = settings.LyricsIdleOpacity;
        _idleNear = Math.Min(1, _idleFar + 0.12);
        _glowIntensity = settings.LyricsGlowIntensity;
        _glowTest = Environment.GetEnvironmentVariable("KODA_GLOW_TEST") ?? "staticmask";
        _glowBlurFixed = _glowTest is "blurfixed" or "opacityonly";
        _glowTransformFixed = _glowTest is "transformfixed" or "opacityonly";
        _glowNoMask = _glowTest == "nomask";
        _glowStaticMask = _glowTest == "staticmask";
        var freeze = Environment.GetEnvironmentVariable("KODA_LYRICS_FREEZE_AT");
        _freezeAt = double.TryParse(
            freeze,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var fixedAt)
            ? fixedAt
            : double.NaN;
        var twinOpacity = Environment.GetEnvironmentVariable("KODA_MASK_TWIN_OPACITY");
        _maskTwinOpacity = double.TryParse(
            twinOpacity,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsedOpacity)
            ? Math.Clamp(parsedOpacity, 0, 1)
            : 0.01;
        _maskHide = Environment.GetEnvironmentVariable("KODA_MASK_HIDE")
            ?? (_glowStaticMask ? "wrapper" : "");
        _glow = _glowIntensity > 0
            && _glowTest != "noglowhost"
            && Environment.GetEnvironmentVariable("KODA_LYRICS_GLOW_OFF") != "1";
        _motion = settings.LyricsMotion
            && Environment.GetEnvironmentVariable("KODA_LYRICS_MOTION_OFF") != "1";
        _renderer = settings.LyricsRenderer;
        UpdateScale();
    }

    /// <summary>The size the words are drawn at: the listener's size, scaled down when the
    /// panel is narrower or shorter than the look is designed for (a small window, UI zoom,
    /// or a high-DPI screen). The setting stays the size the listener chose.</summary>
    private void UpdateScale()
    {
        var width = _panelWidth > 0 ? Math.Max(0, _panelWidth - 24) : DesignWidth;
        var height = _panelHeight > 0 ? _panelHeight : DesignHeight;
        var scale = Math.Clamp(Math.Min(width / DesignWidth, height / DesignHeight), MinScale, 1);
        _fontSize = Math.Max(MinFontSize, Math.Round(_baseFontSize * scale, 1));
        _lineGap = Math.Round(_baseLineGap * scale, 1);
        _risePixels = Math.Max(1.5, _fontSize * RiseRatio);
    }

    /// <summary>Fills the panel with the lyrics of a song.</summary>
    public void Load(Lyrics? lyrics)
    {
        ReadLook();
        _tween.Stop();
        _frame.Stop();
        UserScrolled = false;
        ReleaseGlows();
        ClearGlyphMasks();
        _repaintFrom = -1;
        _repaintTo = -1;
        _lines.Children.Clear();
        _rows.Clear();
        _lyricLines.Clear();
        _leadingDots = null;
        _leadingDotItems = [];
        _current = null;
        _loaded = lyrics is { IsSynced: true };

        if (lyrics is null)
        {
            return;
        }

        // The room above the first line: the first words can sit at the reading position,
        // clear of the fade at the top of the panel.
        _lines.Children.Add(_topSpacer);

        if (!lyrics.IsSynced)
        {
            // Untimed lyrics are shown as they came: one block, no timing.
            var size = _fontSize;
            var plain = new TextBlock
            {
                Text = lyrics.Plain,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush(0xD9),
            };
            Shape(plain, size);
            _lines.Children.Add(plain);
            UpdateSpacers();
            return;
        }

        // A long wait before the first line gets its dots too.
        if (lyrics.Lines.Count > 0 && lyrics.Lines[0].Time >= InterludeSeconds)
        {
            _leadingDotItems = [];
            _leadingDots = MakeDots(_leadingDotItems, _fontSize, _lineGap * 0.75);
            // The opening dots lead into the first line.
            _leadingDots.HorizontalAlignment = SideOf(lyrics.Lines[0]);
            _leadingDots.Tapped += (_, _) =>
            {
                // The intermission before the first line starts with the song.
                UserScrolled = false;
                Seek?.Invoke(0);
            };
            _lines.Children.Add(_leadingDots);
        }

        for (var index = 0; index < lyrics.Lines.Count; index++)
        {
            var line = lyrics.Lines[index];
            var fontSize = _fontSize;
            // A duet's second singer sits on the right. Everyone else sits on the left,
            // including a group singing together: centering those lines made a duet's
            // simultaneous lines read as one block in the middle of the panel.
            var side = SideOf(line);
            var rightAligned = side == HorizontalAlignment.Right;
            var row = new Row
            {
                Line = line,
                Words = line.Words.Count == 0
                    ? new StackPanel()
                    : new WordWrapPanel
                    {
                        RowSpacing = 4,
                        AlignRight = rightAligned,
                        Inset = 0,
                    },
                Background = new WordWrapPanel
                {
                    RowSpacing = 2,
                    Opacity = _idleFar * 0.7,
                    AlignRight = rightAligned,
                    Inset = 0,
                },
            };

            var halfGap = _lineGap / 2;
            var content = new StackPanel { Spacing = 6 };
            var host = new Grid
            {
                Margin = new Thickness(0, halfGap, 0, halfGap),
                // A transparent background makes the whole line box hit-testable, so the
                // hover box appears wherever the pointer is over the line, not only on a glyph.
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            };
            host.HorizontalAlignment = side;
            row.Words.HorizontalAlignment = side;
            row.Background.HorizontalAlignment = side;
            var textSide = side == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Left;

            if (line.Words.Count > 0)
            {
                row.HoverBox = MakeHoverBox(fontSize);
                host.Children.Add(row.HoverBox);
            }

            host.Children.Add(content);
            content.Children.Add(row.Words);

            row.Host = host;

            // A tap on the line jumps the song to it, and hands the panel back to the song.
            host.Tapped += (_, _) =>
            {
                UserScrolled = false;
                Seek?.Invoke(line.Time);
            };

            if (row.HoverBox is not null)
            {
                host.PointerEntered += (_, _) => SetHover(row, true);
                host.PointerExited += (_, _) => SetHover(row, false);
            }

            if (line.Words.Count == 0)
            {
                // Line-timed lyrics: there are no words to fill, so the line is set as text
                // and lights as a whole when the song reaches it. Without this the row was
                // empty, which is how a song could show nothing but the interlude dots.
                var lineText = new TextBlock
                {
                    Text = line.Text.Length == 0 ? "♪" : line.Text,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = textSide,
                    Foreground = Brush(0xFF),
                };
                Shape(lineText, fontSize);
                row.Words.Children.Add(lineText);
            }
            else
            {
                var wrap = (WordWrapPanel)row.Words;
                for (var wordIndex = 0; wordIndex < line.Words.Count; wordIndex++)
                {
                    var item = MakeWord(
                        line.Words[wordIndex],
                        fontSize,
                        joinsPrevious: JoinsPrevious(line, wordIndex));
                    row.WordItems.Add(item);
                    wrap.Add(item.Element, item.JoinsPrevious);
                }

                // A word split into syllables bounces from its inner seam rather than its
                // centre, so the pieces stay attached while the word bounces.
                ShapeSyllables(row.WordItems);
            }

            foreach (var word in line.Background)
            {
                if (_backingMode == BackingVocalsMode.Hidden)
                {
                    break; // the listener turned the backing vocals off
                }

                var text = word.Text;
                if (_backingMode == BackingVocalsMode.WithoutParentheses)
                {
                    text = text.Replace("(", string.Empty).Replace(")", string.Empty).Trim();
                    if (text.Length == 0)
                    {
                        continue; // a bracket on its own is not a word
                    }
                }

                // Backing vocals are drawn at 70% of the sung line, a little lighter.
                var spoken = text == word.Text ? word : word with { Text = text };
                var item = MakeWord(spoken, fontSize * 0.7, soft: true);
                row.BackgroundItems.Add(item);
                row.Background.Add(item.Element, joinsPrevious: false);
            }

            if (row.BackgroundItems.Count > 0)
            {
                ShapeSyllables(row.BackgroundItems);
                content.Children.Add(row.Background);
            }

            foreach (var word in row.WordItems)
            {
                foreach (var letter in word.Letters)
                {
                    letter.RowIndex = index;
                }
            }

            foreach (var word in row.BackgroundItems)
            {
                foreach (var letter in word.Letters)
                {
                    letter.RowIndex = index;
                }
            }

            _lines.Children.Add(host);
            _rows.Add(row);
            _lyricLines.Add(line);

            // A long silence after this line shows three dots that fill until the next one.
            // The gap runs from the line's end, as the Mac measures it: measuring from the
            // line's start put dots between ordinary consecutive lines.
            var next = index + 1 < lyrics.Lines.Count ? lyrics.Lines[index + 1] : null;
            if (next is not null && line.End is { } lineEnd && next.Time - lineEnd >= InterludeSeconds)
            {
                row.DotsHost = MakeDots(row.Dots, fontSize, _lineGap * 0.75);
                // The dots lead into the next line, so in a duet they sit on that line's
                // side, not the side of the singer who just finished.
                row.DotsHost.HorizontalAlignment = SideOf(next);
                var gapStart = lineEnd;
                row.DotsHost.Tapped += (_, _) =>
                {
                    // Back to where the silence begins, so the dots fill from the start.
                    UserScrolled = false;
                    Seek?.Invoke(gapStart);
                };
                _lines.Children.Add(row.DotsHost);
            }
        }

        // The room below the last line, so the final lines of a song can still be scrolled
        // up to the reading position.
        _lines.Children.Add(_spacer);

        // A song opened part way through needs its earlier lines painted as complete and
        // its later lines left unsung, not everything starting grey.
        _needsFullPaint = true;

        ApplyTextWidth();
        UpdateSpacers();
    }

    /// <summary>Which side of the panel a line belongs to. A duet's second singer sits on
    /// the right; everyone else, including a group singing together, sits on the left.</summary>
    private static HorizontalAlignment SideOf(LyricLine line) =>
        line.Voice == LyricVoice.Duet ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    /// <summary>The rounded, translucent box that grows in behind a line under the pointer.
    /// It sits in its own layer, so it never changes the line's measure.</summary>
    private static Border MakeHoverBox(double fontSize)
    {
        var box = new Border
        {
            Background = new SolidColorBrush(HoverBoxColor),
            CornerRadius = new CornerRadius(HoverBoxRadius(fontSize)),
            Margin = new Thickness(-fontSize * HoverBoxPad, -2, -fontSize * HoverBoxPad, -2),
            Opacity = 0,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        box.Loaded += (_, _) =>
        {
            var visual = ElementCompositionPreview.GetElementVisual(box);
            visual.Opacity = 0;
            visual.Scale = new Vector3(0.94f, 0.94f, 1);
        };

        box.SizeChanged += (_, e) =>
        {
            var visual = ElementCompositionPreview.GetElementVisual(box);
            visual.CenterPoint = new Vector3(
                (float)(e.NewSize.Width / 2),
                (float)(e.NewSize.Height / 2),
                0);
        };

        return box;
    }

    /// <summary>The corner radius follows the lyric size, so the box stays proportional
    /// when the panel scales the words down or the listener chooses a larger size.</summary>
    private static double HoverBoxRadius(double fontSize) =>
        Math.Clamp(fontSize * HoverBoxRadiusRatio, HoverBoxRadiusMin, HoverBoxRadiusMax);

    /// <summary>Points the syllables of a split word at their shared seam: the first grows
    /// from its right edge, the last from its left, and the middle stays centred.</summary>
    private static void ShapeSyllables(IReadOnlyList<Word> words)
    {
        var start = 0;
        while (start < words.Count)
        {
            var end = start;
            while (end + 1 < words.Count && words[end + 1].JoinsPrevious)
            {
                end++;
            }

            var count = end - start + 1;
            for (var index = start; index <= end; index++)
            {
                var word = words[index];
                word.PushX = count == 1
                    ? 0
                    : index == start
                        ? -LyricsMotion.WordSpread
                        : index == end
                            ? LyricsMotion.WordSpread
                            : 0;
                word.Element.RenderTransformOrigin = count == 1
                    ? new Windows.Foundation.Point(0.5, 0.5)
                    : index == start
                        ? new Windows.Foundation.Point(1, 0.5)
                        : index == end
                            ? new Windows.Foundation.Point(0, 0.5)
                            : new Windows.Foundation.Point(0.5, 0.5);
            }

            start = end + 1;
        }
    }

    /// <summary>Three dots that fill in turn through an instrumental gap; they grow with
    /// the words so the pause reads at any size. They sit in the flow at half opacity even
    /// when their gap is not playing, so the interlude is already placed.</summary>
    private static StackPanel MakeDots(List<Ellipse> dots, double fontSize, double verticalMargin)
    {
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Opacity = 0.5,
            RenderTransform = new CompositeTransform(),
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
        };
        var diameter = DotsDiameter(fontSize);
        for (var dot = 0; dot < 3; dot++)
        {
            var ellipse = new Ellipse
            {
                Width = diameter,
                Height = diameter,
                Fill = Brush(0xFF),
                Opacity = 0x4D / 255.0,
            };
            host.Children.Add(ellipse);
            dots.Add(ellipse);
        }

        SizeDots(host, dots, fontSize, verticalMargin);
        return host;
    }

    private static double DotsDiameter(double fontSize) => Math.Max(6, Math.Round(fontSize * 0.32));

    private static void SizeDots(StackPanel host, List<Ellipse> dots, double fontSize, double verticalMargin)
    {
        var diameter = DotsDiameter(fontSize);
        host.Spacing = Math.Round(diameter * 0.75);
        host.Margin = new Thickness(0, verticalMargin, 0, verticalMargin);
        foreach (var dot in dots)
        {
            dot.Width = diameter;
            dot.Height = diameter;
        }
    }

    /// <summary>One word: its element, the mask it fills through, and its rise. A held
    /// syllable in the Expressive look is drawn as a row of letters so each can wave.</summary>
    private Word MakeWord(LyricWord word, double fontSize, bool soft = false, bool joinsPrevious = false)
    {
        var transform = new CompositeTransform();
        var pieces = _renderer == LyricsRenderer.Expressive && _motion && LyricsMotion.IsHeldSyllable(word)
            ? LyricsMotion.WaveLetters(word.Text)
            : null;

        if (pieces is { Count: > 0 })
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, word.SpaceAfter ? fontSize * WordSpacing : 0, 0),
                RenderTransform = transform,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            };

            var letters = new List<Letter>(pieces.Count);
            for (var index = 0; index < pieces.Count; index++)
            {
                var letter = MakeLetter(pieces[index], fontSize, soft, index, pieces.Count);
                letters.Add(letter);
                panel.Children.Add(letter.Element);
            }

            panel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

            var split = new Word
            {
                Data = word,
                Element = panel,
                Text = null,
                Fill = null,
                Transform = transform,
                JoinsPrevious = joinsPrevious,
                FontSize = fontSize,
            };
            split.Letters.AddRange(letters);
            return split;
        }

        var fill = NewFill();
        var element = new TextBlock
        {
            // The space between words is a margin, not a space in the text: a trailing
            // space in a text block does not draw, and two syllables of one word must
            // sit together.
            Text = word.Text,
            Margin = new Thickness(0, 0, word.SpaceAfter ? fontSize * WordSpacing : 0, 0),
            Foreground = fill,
            RenderTransform = transform,
            TextWrapping = TextWrapping.NoWrap,
        };
        Shape(element, fontSize, soft);

        element.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

        return new Word
        {
            Data = word,
            Element = element,
            Text = element,
            Fill = fill,
            Transform = transform,
            JoinsPrevious = joinsPrevious,
            FontSize = fontSize,
        };
    }

    /// <summary>One letter of a held syllable. An empty layer behind the letter hosts a
    /// Composition DropShadow masked by the glyph alpha, which is a real Gaussian glow.</summary>
    private Letter MakeLetter(string text, double fontSize, bool soft, int index, int count)
    {
        var fill = NewFill();
        var transform = new CompositeTransform();
        var textBlock = new TextBlock
        {
            Text = text,
            Foreground = fill,
            RenderTransform = transform,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            TextWrapping = TextWrapping.NoWrap,
        };
        Shape(textBlock, fontSize, soft);

        var host = new Grid();
        Grid? glowHost = null;
        CompositeTransform? glowTransform = null;
        TextBlock? maskText = null;

        if (_glow)
        {
            // The glow host is the first child, so its sprite draws behind the sharp
            // letter. SetElementChildVisual adds the sprite on top of this empty layer,
            // not on top of the text in the sibling layer.
            glowTransform = new CompositeTransform();
            glowHost = new Grid
            {
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                RenderTransform = glowTransform,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            };
            host.Children.Add(glowHost);

            if (_glowStaticMask)
            {
                // Test only: a twin whose fill is a fixed solid, so the mask source is
                // never invalidated by the sharp letter's per-frame gradient. It is
                // nearly transparent but still rendered, which GetAlphaMask needs.
                maskText = new TextBlock
                {
                    Text = text,
                    Foreground = new SolidColorBrush(MaskTwinColor),
                    IsHitTestVisible = false,
                    Opacity = _maskHide.Length > 0 ? 1 : _maskTwinOpacity,
                    TextWrapping = TextWrapping.NoWrap,
                };
                Shape(maskText, fontSize, soft);

                if (_maskHide.Length > 0)
                {
                    var wrapper = new Grid
                    {
                        IsHitTestVisible = false,
                    };
                    wrapper.Children.Add(maskText);

                    switch (_maskHide)
                    {
                        case "wrapper":
                            wrapper.Opacity = 0.01;
                            break;
                        case "visual":
                            ElementCompositionPreview.GetElementVisual(wrapper).Opacity = 0f;
                            break;
                        case "visual01":
                            ElementCompositionPreview.GetElementVisual(wrapper).Opacity = 0.01f;
                            break;
                        case "clip":
                            wrapper.Clip = new RectangleGeometry
                            {
                                Rect = new Windows.Foundation.Rect(0, 0, 0, 0),
                            };
                            break;
                        case "offscreen":
                            wrapper.RenderTransform = new TranslateTransform { X = -10000 };
                            break;
                    }

                    glowHost.Children.Add(wrapper);
                }
                else
                {
                    glowHost.Children.Add(maskText);
                }
            }
        }

        host.Children.Add(textBlock);
        host.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

        var letter = new Letter
        {
            Element = host,
            Text = textBlock,
            MaskText = maskText,
            Fill = fill,
            Transform = transform,
            GlowHost = glowHost,
            GlowTransform = glowTransform,
            Index = index,
            Count = count,
        };

        if (glowHost is not null)
        {
            textBlock.SizeChanged += (_, _) =>
            {
                letter.NeedsMaskRefresh = true;
                if (letter.Shadow is not null)
                {
                    RefreshGlowMask(letter);
                }
            };
        }

        return letter;
    }

    private static LinearGradientBrush NewFill() => new()
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(1, 0),
        GradientStops =
        {
            new GradientStop { Color = UnsungColor, Offset = 0 },
            new GradientStop { Color = UnsungColor, Offset = 0 },
            new GradientStop { Color = UnsungColor, Offset = 0.0001 },
        },
    };

    /// <summary>Whether a timed word is a syllable of the one before it: the provider gave
    /// them no space and the line's own text shows them as one word. A provider that lost
    /// every space in its timings does not glue real words together.</summary>
    private static bool JoinsPrevious(LyricLine line, int index)
    {
        if (index <= 0 || line.Words[index - 1].SpaceAfter)
        {
            return false;
        }

        var joined = line.Words[index - 1].Text + line.Words[index].Text;
        return joined.Length <= 12 &&
            line.Text.Contains(joined, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Re-shapes the words already on the panel when the fitted text size changes
    /// (a narrower window, a DPI change or UI zoom), keeping the same elements.</summary>
    private void Reshape()
    {
        var sidePad = Math.Max(18, _fontSize * 0.5);
        var endPad = Math.Max(24, sidePad);
        _lines.Margin = new Thickness(sidePad, 0, endPad, 0);
        var lineWidth = Math.Max(0, _panelWidth - sidePad - endPad);
        foreach (var row in _rows)
        {
            row.Host.Margin = new Thickness(0, _lineGap / 2, 0, _lineGap / 2);
            if (row.HoverBox is { } hoverBox)
            {
                hoverBox.Margin = new Thickness(
                    -_fontSize * HoverBoxPad, -2, -_fontSize * HoverBoxPad, -2);
                hoverBox.CornerRadius = new CornerRadius(HoverBoxRadius(_fontSize));
            }

            foreach (var word in row.WordItems)
            {
                ReshapeWord(word, _fontSize, soft: false);
            }

            foreach (var word in row.BackgroundItems)
            {
                ReshapeWord(word, _fontSize * 0.7, soft: true);
            }

            if (row.WordItems.Count == 0)
            {
                foreach (var text in row.Words.Children.OfType<TextBlock>())
                {
                    Shape(text, _fontSize);
                    text.MaxWidth = lineWidth;
                }
            }

            if (row.DotsHost is { } dots)
            {
                SizeDots(dots, row.Dots, _fontSize, _lineGap * 0.75);
            }
        }

        if (_leadingDots is { } leading)
        {
            SizeDots(leading, _leadingDotItems, _fontSize, _lineGap * 0.75);
        }

        // The shaped glyphs changed size or weight: their cached masks no longer match.
        ClearGlyphMasks();
    }

    private void ReshapeWord(Word word, double size, bool soft)
    {
        if (word.Letters.Count > 0)
        {
            foreach (var letter in word.Letters)
            {
                Shape(letter.Text, size, soft);
                if (letter.MaskText is { } maskText)
                {
                    Shape(maskText, size, soft);
                }

                letter.PaintedLit = double.NaN;
                letter.NeedsMaskRefresh = true;
            }
        }
        else if (word.Text is { } text)
        {
            Shape(text, size, soft);
        }

        word.FontSize = size;
        word.Element.Margin = new Thickness(0, 0, word.Data.SpaceAfter ? size * WordSpacing : 0, 0);
        word.PaintedRise = -1; // the lift distance changed with the size
        word.PaintedScale = -1;
        word.PaintedLit = double.NaN;
    }

    /// <summary>The pointer entered or left a line: grow the box in or out and repaint the
    /// line so its text brightens at once, even while the song is paused.</summary>
    private void SetHover(Row row, bool on)
    {
        if (row.HoverBox is not { } box)
        {
            return;
        }

        row.Hovered = on;
        var visual = ElementCompositionPreview.GetElementVisual(box);
        EnsureHoverAnimations(visual.Compositor);

        visual.StartAnimation("Opacity", on ? _hoverOpacityIn! : _hoverOpacityOut!);
        visual.StartAnimation("Scale", on ? _hoverScaleIn! : _hoverScaleOut!);

        Paint();
    }

    /// <summary>The hover animations are identical on every line. Build them once and
    /// reuse them instead of allocating two animations for each pointer movement.</summary>
    private void EnsureHoverAnimations(Compositor compositor)
    {
        if (_hoverEase is not null)
        {
            return;
        }

        _hoverEase = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.22f, 1f),
            new Vector2(0.36f, 1f));

        _hoverOpacityIn = compositor.CreateScalarKeyFrameAnimation();
        _hoverOpacityIn.InsertKeyFrame(1, 1f, _hoverEase);
        _hoverOpacityIn.Duration = TimeSpan.FromMilliseconds(250);

        _hoverOpacityOut = compositor.CreateScalarKeyFrameAnimation();
        _hoverOpacityOut.InsertKeyFrame(1, 0f, _hoverEase);
        _hoverOpacityOut.Duration = TimeSpan.FromMilliseconds(250);

        _hoverScaleIn = compositor.CreateVector3KeyFrameAnimation();
        _hoverScaleIn.InsertKeyFrame(1, Vector3.One, _hoverEase);
        _hoverScaleIn.Duration = TimeSpan.FromMilliseconds(400);

        _hoverScaleOut = compositor.CreateVector3KeyFrameAnimation();
        _hoverScaleOut.InsertKeyFrame(1, new Vector3(0.94f, 0.94f, 1f), _hoverEase);
        _hoverScaleOut.Duration = TimeSpan.FromMilliseconds(300);
    }

    /// <summary>Gives every word panel the width to wrap against, and the line-timed text
    /// the width to wrap inside. Called from the width the scroll viewer actually has.</summary>
    private void ApplyTextWidth()
    {
        if (_panelWidth <= 0)
        {
            return;
        }

        var sidePad = Math.Max(18, _fontSize * 0.5);
        var endPad = Math.Max(24, sidePad);
        _lines.Margin = new Thickness(sidePad, 0, endPad, 0);
        var contentWidth = Math.Max(0, _panelWidth - sidePad - endPad);
        foreach (var row in _rows)
        {
            if (row.Words is WordWrapPanel wrap)
            {
                wrap.WrapWidth = contentWidth;
            }
            else
            {
                foreach (var text in row.Words.Children.OfType<TextBlock>())
                {
                    text.MaxWidth = contentWidth;
                }
            }

            row.Background.WrapWidth = contentWidth;
        }
    }

    /// <summary>The room above and below the words: the first and last lines can reach the
    /// reading position, and the fade at the top never swallows the opening lines.</summary>
    private void UpdateSpacers()
    {
        _topSpacer.Height = Math.Max(0, _scroll.ViewportHeight * ReadingAnchor);
        _spacer.Height = Math.Max(0, _scroll.ViewportHeight);
    }

    /// <summary>Puts the panel on screen with a short fade, and starts it following the song.</summary>
    public void PlayEntrance()
    {
        _visible = true;
        UserScrolled = false;
        UpdateFrame();

        // The rows were just filled: let the panel measure them before placing the line.
        // Without the layout pass the glide aims past the height the panel had before the
        // words arrived, ChangeView clamps it, and the words stay at the top of the song.
        _scroll.UpdateLayout();

        var previousSize = _fontSize;
        UpdateScale();
        if (Math.Abs(_fontSize - previousSize) >= 0.25)
        {
            Reshape();
        }

        _scroll.UpdateLayout();

        // The panel has just come into view: light the line being sung and place it now
        // that the panel knows its height (before that, there was nothing to measure against).
        Follow();
        if (_current is not null)
        {
            JumpTo(_current);
        }

        // The fade is the app's own pattern: a storyboard on the element's Opacity.
        // (A transform created in code cannot be a storyboard's target - only a
        // named one, declared in XAML, can.)
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(280),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, _lines);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        _lines.Opacity = 0;
        storyboard.Begin();
    }

    /// <summary>The panel is closed: it stops reading the song.</summary>
    public void Pause()
    {
        _visible = false;
        UpdateFrame();
    }

    /// <summary>Whether the song is playing. The panel only repaints while it is, so a
    /// paused song does not keep a 60-frame timer awake.</summary>
    public void SetPlaying(bool playing)
    {
        _playing = playing;
        UpdateFrame();
    }

    private void UpdateFrame()
    {
        var run = _visible && _playing && _rows.Count > 0;
        if (run && !_frame.IsRunning)
        {
            _frame.Start();
        }
        else if (!run && _frame.IsRunning)
        {
            _frame.Stop();
        }
    }

    /// <summary>Follows the song: lights the line being sung and glides to it.</summary>
    public void Follow()
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var index = Lyrics.IndexOf(_position, _lyricLines) ?? -1;
        if (index < 0)
        {
            // Before the first line (the intro): still paint, so its dots fill.
            // A seek into the intro should bring the upcoming line into view now,
            // not wait for its first word to start several seconds later.
            if (_current is not null
                && _rows.Count > 0
                && !ReferenceEquals(_current, _rows[0])
                && !_userScrolled)
            {
                _current = _rows[0];
                GlideTo(_current);
            }

            Paint();
            return;
        }

        if (index >= _rows.Count)
        {
            return;
        }

        var row = _rows[index];
        if (!ReferenceEquals(row, _current))
        {
            _current = row;
            if (!_userScrolled)
            {
                GlideTo(row);
            }
        }

        // Also runs after a seek: the window was invalidated so the target line's
        // freed glows are rebuilt even when the line index did not change.
        ScheduleGlows(index);
        Paint(index);
    }

    /// <summary>
    /// The words of the line being sung, redrawn every frame: the mask runs across the
    /// word, the word rises as it starts, a note held long swells and glows.
    /// </summary>
    private void Paint(int? knownIndex = null)
    {
        // The lyrics always fit the room they are actually given, even if the size events
        // for a pane/display/zoom change never arrived.
        SyncSize();

        // Spread shadow setup for the next line over the frames while the current line
        // is singing, so a long note never pays for its mask on the frame it starts.
        WarmQueuedGlows();

        if (_needsGlide && _current is not null && _scroll.ViewportHeight > 1)
        {
            _needsGlide = false;
            GlideTo(_current);
        }

        var time = _position;
        _paintedLetters = 0;
        if (_needsFullPaint)
        {
            _needsFullPaint = false;
            _repaintFrom = -1;
            _repaintTo = -1;
            RepaintWords(time);
        }
        else if (_repaintFrom >= 0 && _repaintTo >= _repaintFrom)
        {
            var from = _repaintFrom;
            var to = _repaintTo;
            _repaintFrom = -1;
            _repaintTo = -1;
            RepaintRange(from, to, time);
        }

        var index = knownIndex ?? (Lyrics.IndexOf(time, _lyricLines) ?? -1);
        PaintInterlude(time, index >= 0 ? index : null);

        var motion = _motion;
        var expressive = _renderer == LyricsRenderer.Expressive;
        var singing = index >= 0 && index < _rows.Count && time <= LineEnd(_rows[index]) + 0.05;
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var sung = IsSung(row, time);
            var isActive = singing && i == index;
            var distance = index >= 0 ? Math.Abs(i - index) : int.MaxValue;
            row.Opacity = expressive
                ? isActive
                    ? 1
                    : index >= 0 && i <= index
                        ? ExpressiveSungLineOpacity
                        : _idleFar
                : sung
                    ? 1
                    : distance <= 1
                        ? _idleNear
                        : _idleFar;

            // The rows step back with a fade alone: scaling them by a hair made a new line
            // snap when the fill took over the transform. While paused the fade would only
            // creep (there is no frame timer), so the row takes its target at once.
            var wordsTarget = row.Hovered ? 1 : row.Opacity;
            if (Math.Abs(row.Words.Opacity - wordsTarget) >= 0.001)
            {
                row.Words.Opacity = _playing
                    ? Approach(row.Words.Opacity, wordsTarget, expressive ? 0.03 : 0.18)
                    : wordsTarget;
            }

            if (row.BackgroundItems.Count > 0)
            {
                // Backing vocals step back with their line, at 70% of its weight.
                var target = wordsTarget * 0.7;
                if (Math.Abs(row.Background.Opacity - target) >= 0.001)
                {
                    row.Background.Opacity = _playing
                        ? Approach(row.Background.Opacity, target, expressive ? 0.03 : 0.18)
                        : target;
                }
            }

            if (expressive)
            {
                // The Spicy springs keep moving for a while after the line's last word, so
                // keep painting the line through its tail instead of freezing mid-settle.
                if (!isActive && !IsWithinTail(row, time))
                {
                    continue;
                }
            }
            else if (!sung)
            {
                continue;
            }

            // Every line still being sung keeps filling, not only the latest one: a held
            // note outlasts the line break, and two lines may be sung at the same time.
            foreach (var word in row.WordItems)
            {
                Paint(word, time, motion, hovered: row.Hovered, active: isActive);
            }

            foreach (var word in row.BackgroundItems)
            {
                Paint(word, time, motion, hovered: row.Hovered, active: isActive);
            }
        }
    }

    /// <summary>The end of a line: the provider's end, or its last word's end.</summary>
    private static double LineEnd(Row row) =>
        row.Line.End ?? (row.WordItems.Count > 0 ? row.WordItems[^1].Data.End : row.Line.Time);

    /// <summary>Whether a line is still inside the Expressive settle window.</summary>
    private static bool IsWithinTail(Row row, double time) =>
        time >= row.Line.Time && time <= LineEnd(row) + ExpressiveTailSeconds;

    /// <summary>Whether a line's own time still covers the song's position: a line that
    /// started keeps filling until its end, even after a later line has started.</summary>
    private static bool IsSung(Row row, double time) =>
        time >= row.Line.Time && time <= LineEnd(row) + 0.05;

    /// <summary>Repaints every word after a seek, so lines ahead are unsung and the lines
    /// already behind are complete, instead of keeping the old line's partial fill.</summary>
    private void RepaintWords(double time)
    {
        foreach (var row in _rows)
        {
            var started = time >= row.Line.Time;
            foreach (var word in row.WordItems)
            {
                Paint(word, time, _motion, lineStarted: started);
            }

            foreach (var word in row.BackgroundItems)
            {
                Paint(word, time, _motion, lineStarted: started);
            }
        }
    }

    /// <summary>Queues only the rows that can have changed between two song positions.
    /// Everything before both positions is sung, everything after both is unsung, so
    /// the old-to-new span (plus a small margin for tails) is all that needs a repaint.</summary>
    private void SetRepaintRange(int oldIndex, int newIndex)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var from = Math.Max(0, Math.Min(oldIndex, newIndex) - 2);
        var to = Math.Min(_rows.Count - 1, Math.Max(oldIndex, newIndex) + 2);
        if (from > to)
        {
            return;
        }

        _repaintFrom = _repaintFrom < 0 ? from : Math.Min(_repaintFrom, from);
        _repaintTo = Math.Max(_repaintTo, to);
    }

    private void RepaintRange(int from, int to, double time)
    {
        for (var i = from; i <= to && i < _rows.Count; i++)
        {
            var row = _rows[i];
            var started = time >= row.Line.Time;
            foreach (var word in row.WordItems)
            {
                Paint(word, time, _motion, lineStarted: started);
            }

            foreach (var word in row.BackgroundItems)
            {
                Paint(word, time, _motion, lineStarted: started);
            }
        }
    }

    /// <summary>
    /// The three dots of a gap: before the first line, or after the line that just ended,
    /// while the gap lasts. Only one gap is ever on screen, as on the Mac. The gap is
    /// measured from the line's end; measuring from its start put dots after every line.
    /// </summary>
    private void PaintInterlude(double time, int? current)
    {
        Row? active = null;
        var leading = false;
        var progress = 0.0;

        if (current is null)
        {
            if (_lyricLines.Count > 0 && _lyricLines[0].Time >= InterludeSeconds && time >= 0)
            {
                leading = true;
                progress = Math.Clamp(time / _lyricLines[0].Time, 0, 1);
            }
        }
        else
        {
            var index = current.Value;
            if (index + 1 < _lyricLines.Count &&
                _lyricLines[index].End is { } end &&
                _lyricLines[index + 1].Time - end >= InterludeSeconds &&
                time > end + 0.3)
            {
                active = _rows[index];
                progress = Math.Clamp((time - end) / (_lyricLines[index + 1].Time - end), 0, 1);
                // The line just sung rests while the gap plays, as it does on the Mac.
                active.Opacity = _renderer == LyricsRenderer.Expressive
                    ? ExpressiveSungLineOpacity
                    : _idleNear;
            }
        }

        foreach (var row in _rows)
        {
            if (row.DotsHost is null)
            {
                continue;
            }

            // The dots dim and lift like a lyric row, with no separate storyboard and no
            // second opacity to snap to when the gap ends.
            var target = ReferenceEquals(row, active) ? 1 : 0.5;
            row.DotsHost.Opacity = _playing
                ? Approach(row.DotsHost.Opacity, target)
                : target;
            BreatheDots(row.DotsHost, ReferenceEquals(row, active), time);
        }

        if (active is not null)
        {
            FillDots(active.Dots, progress);
        }
        else if (current is { } index && index < _rows.Count)
        {
            // Coming back out of a gap (a seek, a replay) lights the line again.
            _rows[index].Opacity = 1;
        }

        if (_leadingDots is not null)
        {
            var target = leading ? 1 : 0.5;
            _leadingDots.Opacity = _playing
                ? Approach(_leadingDots.Opacity, target)
                : target;
            BreatheDots(_leadingDots, leading, time);

            if (leading)
            {
                FillDots(_leadingDotItems, progress);
            }
        }

        _interludeActive = active is not null || leading;
    }

    /// <summary>The Spicy dots breathe while their gap plays: 0.92 to 1.08 on a 2.4s cycle.</summary>
    private void BreatheDots(StackPanel? host, bool active, double time)
    {
        if (host?.RenderTransform is not CompositeTransform transform)
        {
            return;
        }

        var scale = active && _renderer == LyricsRenderer.Expressive
            ? 1 - 0.08 * Math.Cos(2 * Math.PI * time / LyricsMotion.DotsBreatheSeconds)
            : 1;
        if (Math.Abs(transform.ScaleX - scale) >= 0.001)
        {
            transform.ScaleX = scale;
            transform.ScaleY = scale;
        }
    }

    private static void FillDots(IReadOnlyList<Ellipse> dots, double progress)
    {
        for (var dot = 0; dot < dots.Count; dot++)
        {
            var fill = Math.Clamp(progress * dots.Count - dot, 0, 1);
            var opacity = (0x4D + (0xFF - 0x4D) * fill) / 255.0;
            // Keep the dot's brush constant and move only its opacity, in visible
            // steps. Rewriting the SolidColorBrush.Color every frame made the intro
            // re-rasterize three brushes continuously while it waited for the first line.
            if (Math.Abs(dots[dot].Opacity - opacity) >= 0.01)
            {
                dots[dot].Opacity = opacity;
            }
        }
    }

    /// <summary>One word: how much of it is sung, how far it has risen, how much it glows.</summary>
    private void Paint(
        Word word,
        double time,
        bool motion,
        bool lineStarted = true,
        bool hovered = false,
        bool active = false)
    {
        if (_renderer == LyricsRenderer.Expressive)
        {
            PaintExpressive(word, time, motion, hovered, active);
            return;
        }

        var data = word.Data;
        var classic = _renderer == LyricsRenderer.Classic;
        // Classic lights the whole line; the sweep fills each word as it is sung.
        var fill = classic
            ? (lineStarted ? 1 : 0)
            : Math.Clamp((time - data.Start) / Math.Max(0.001, data.End - data.Start), 0, 1);
        var rise = motion && _renderer == LyricsRenderer.Sweep ? Math.Clamp((time - data.Start) / RiseSeconds, 0, 1) : 0;
        var eased = 1 - Math.Pow(1 - rise, 3); // quick at first, settling gently
        var sustainStrength = motion && !classic ? Math.Clamp((data.End - data.Start - SustainStart) / SustainStart, 0, 1) : 0;
        var sustain = sustainStrength * Math.Sin(Math.PI * fill);

        // The mask runs from -feather to 1, so the word is clear at the start and full at the end.
        var feather = classic ? 0.0 : _feather;
        var front = fill * (1 + feather);
        if (Math.Abs(fill - word.PaintedFill) >= 0.0005)
        {
            // An unsung word is wholly grey, including a glyph that overhangs its box on
            // the left (a "j"); otherwise the clamped first stop leaves a white sliver.
            // A fully sung word is wholly white, and only the moving front is a gradient.
            word.PaintedFill = fill;
            var stops = word.Fill!.GradientStops;
            if (fill <= 0)
            {
                stops[0].Color = UnsungColor;
                stops[1].Color = UnsungColor;
                stops[2].Color = UnsungColor;
                stops[1].Offset = 0;
                stops[2].Offset = 1;
            }
            else if (fill >= 1)
            {
                stops[0].Color = SungColor;
                stops[1].Color = SungColor;
                stops[2].Color = SungColor;
                stops[1].Offset = 1;
                stops[2].Offset = 1;
            }
            else
            {
                stops[0].Color = SungColor;
                stops[1].Color = SungColor;
                stops[2].Color = UnsungColor;
                stops[1].Offset = Math.Max(0, front - feather);
                stops[2].Offset = Math.Min(1, Math.Max(front, 0.0001));
            }
        }

        // The word rises as it is sung: a ripple through the line.
        if (Math.Abs(eased - word.PaintedRise) >= 0.001)
        {
            word.PaintedRise = eased;
            word.Transform.TranslateY = -_risePixels * eased;
        }

        // A note held long swells, by as much as the look asks for.
        var glow = _glow ? Math.Clamp(_glowIntensity, 0, 2) : 0;
        var swell = 1 + SwellScale * sustain * glow * (classic ? 0 : 1);
        if (Math.Abs(swell - word.PaintedSwell) >= 0.0005)
        {
            word.PaintedSwell = swell;
            word.Transform.ScaleX = swell;
            word.Transform.ScaleY = swell;
        }
    }

    /// <summary>
    /// The Expressive word: Spicy's bounce on the word, and on a held syllable the letter
    /// wave on top of it. The highlight's bright edge runs from -20% to 100% of the word
    /// (or of each letter), with the soft edge a feather behind it.
    /// </summary>
    private void PaintExpressive(Word word, double time, bool motion, bool hovered, bool active)
    {
        var data = word.Data;
        var duration = Math.Max(0.001, data.End - data.Start);
        var elapsed = time - data.Start;
        var progress = Math.Clamp(elapsed / duration, 0, 1);
        var lit = -0.2 + 1.2 * progress;
        var soft = lit + _feather;

        var sung = hovered
            ? active
                ? ExpressiveActiveHoverSung
                : ExpressiveHoverSung
            : ExpressiveSung;
        var unsung = hovered
            ? active
                ? ExpressiveActiveHoverUnsung
                : ExpressiveHoverUnsung
            : ExpressiveUnsung;

        var wordMotion = motion
            ? LyricsMotion.Word(elapsed, duration, word.PushX)
            : new WordMotion(1, 0, 0);
        SetWordMotion(word, wordMotion);

        if (word.Letters.Count > 0)
        {
            _paintedLetters += word.Letters.Count;
            var strength = _glow ? Math.Clamp(_glowIntensity, 0, 2) : 0;
            foreach (var letter in word.Letters)
            {
                var letterMotion = motion
                    ? LyricsMotion.Letter(elapsed, duration, letter.Index, letter.Count)
                    : new LetterMotion(1, 0, 0, 0);

                var start = (double)letter.Index / letter.Count;
                var end = (double)(letter.Index + 1) / letter.Count;
                var localLit = (lit - start) / (end - start);
                var localSoft = (soft - start) / (end - start);

                if (NeedsFill(letter.PaintedLit, lit, letter.PaintedHovered, hovered, letter.PaintedActive, active))
                {
                    PaintGradient(letter.Fill, localLit, localSoft, sung, unsung);
                    letter.PaintedLit = lit;
                    letter.PaintedHovered = hovered;
                    letter.PaintedActive = active;
                }

                SetLetterMotion(letter, letterMotion, word.FontSize, strength);
            }
        }
        else if (word.Fill is { } fill &&
            NeedsFill(word.PaintedLit, lit, word.PaintedHovered, hovered, word.PaintedActive, active))
        {
            PaintGradient(fill, lit, soft, sung, unsung);
            word.PaintedLit = lit;
            word.PaintedHovered = hovered;
            word.PaintedActive = active;
        }
    }

    private static bool NeedsFill(
        double paintedLit,
        double lit,
        bool paintedHovered,
        bool hovered,
        bool paintedActive,
        bool active) =>
        double.IsNaN(paintedLit)
        || Math.Abs(paintedLit - lit) >= 0.0005
        || paintedHovered != hovered
        || paintedActive != active;

    /// <summary>Paints a three-stop brush whose bright edge is at <paramref name="lit"/>
    /// and whose soft edge is at <paramref name="soft"/>, in local brush coordinates.</summary>
    private static void PaintGradient(
        LinearGradientBrush brush,
        double lit,
        double soft,
        Color sung,
        Color unsung)
    {
        var stops = brush.GradientStops;
        if (lit >= 1)
        {
            stops[0].Color = sung;
            stops[1].Color = sung;
            stops[2].Color = sung;
            stops[1].Offset = 1;
            stops[2].Offset = 1;
            return;
        }

        if (soft <= 0)
        {
            stops[0].Color = unsung;
            stops[1].Color = unsung;
            stops[2].Color = unsung;
            stops[1].Offset = 0;
            stops[2].Offset = 1;
            return;
        }

        var feather = Math.Max(0.0001, soft - lit);
        // When an edge falls outside the word, the visible end is a blend, not the
        // saturated end color; otherwise the word's first or last pixel springs to full
        // white a little too early.
        var startColor = lit <= 0
            ? Blend(sung, unsung, -lit / feather)
            : sung;
        var endColor = soft >= 1
            ? Blend(sung, unsung, (1 - lit) / feather)
            : unsung;

        if (lit > 0)
        {
            stops[0].Color = sung;
            stops[1].Color = sung;
            stops[1].Offset = Math.Clamp(lit, 0, 1);
        }
        else
        {
            stops[0].Color = startColor;
            stops[1].Color = startColor;
            stops[1].Offset = 0;
        }

        if (soft < 1)
        {
            stops[2].Color = unsung;
            stops[2].Offset = Math.Clamp(soft, 0, 1);
        }
        else
        {
            stops[2].Color = endColor;
            stops[2].Offset = 1;
        }
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (byte)(from.A + (to.A - from.A) * t),
            (byte)(from.R + (to.R - from.R) * t),
            (byte)(from.G + (to.G - from.G) * t),
            (byte)(from.B + (to.B - from.B) * t));
    }

    private static void SetWordMotion(Word word, WordMotion motion)
    {
        if (Math.Abs(motion.Scale - word.PaintedScale) >= 0.0005)
        {
            word.PaintedScale = motion.Scale;
            word.Transform.ScaleX = motion.Scale;
            word.Transform.ScaleY = motion.Scale;
        }

        var x = motion.X * word.FontSize;
        if (double.IsNaN(word.PaintedX) || Math.Abs(x - word.PaintedX) >= 0.01)
        {
            word.PaintedX = x;
            word.Transform.TranslateX = x;
        }

        var y = motion.Y * word.FontSize;
        if (double.IsNaN(word.PaintedY) || Math.Abs(y - word.PaintedY) >= 0.01)
        {
            word.PaintedY = y;
            word.Transform.TranslateY = y;
        }
    }

    private void SetLetterMotion(Letter letter, LetterMotion motion, double fontSize, double glowStrength)
    {
        if (Math.Abs(motion.Scale - letter.PaintedScale) >= 0.0005)
        {
            letter.PaintedScale = motion.Scale;
            letter.Transform.ScaleX = motion.Scale;
            letter.Transform.ScaleY = motion.Scale;
            if (!_glowTransformFixed && letter.GlowTransform is { } glowTransform)
            {
                glowTransform.ScaleX = motion.Scale;
                glowTransform.ScaleY = motion.Scale;
            }
        }

        var x = motion.X * fontSize;
        if (double.IsNaN(letter.PaintedX) || Math.Abs(x - letter.PaintedX) >= 0.01)
        {
            letter.PaintedX = x;
            letter.Transform.TranslateX = x;
            if (!_glowTransformFixed && letter.GlowTransform is { } glowTransform)
            {
                glowTransform.TranslateX = x;
            }
        }

        var y = motion.Y * fontSize;
        if (double.IsNaN(letter.PaintedY) || Math.Abs(y - letter.PaintedY) >= 0.01)
        {
            letter.PaintedY = y;
            letter.Transform.TranslateY = y;
            if (!_glowTransformFixed && letter.GlowTransform is { } glowTransform)
            {
                glowTransform.TranslateY = y;
            }
        }

        if (letter.NeedsMaskRefresh && letter.Shadow is not null)
        {
            RefreshGlowMask(letter);
        }

        var glow = motion.Glow * glowStrength;
        if (glow > 0.01)
        {
            EnsureGlow(letter);
            if (letter.Shadow is { } shadow)
            {
                var opacity = (float)Math.Clamp(glow, 0, 1);
                var blur = _glowBlurFixed ? 12f : (float)(4 + 12 * opacity);
                if (Math.Abs(opacity - letter.PaintedGlow) >= 0.01)
                {
                    shadow.Opacity = opacity;
                    letter.PaintedGlow = opacity;
                }

                if (Math.Abs(blur - letter.PaintedBlur) >= 0.25)
                {
                    shadow.BlurRadius = blur;
                    letter.PaintedBlur = blur;
                }

                letter.GlowShown = true;
            }
        }
        else
        {
            var idleBlur = _glowBlurFixed ? 12f : 0f;
            if (letter.Shadow is { } idleShadow
                && (letter.PaintedGlow != 0 || letter.PaintedBlur != idleBlur))
            {
                idleShadow.Opacity = 0;
                idleShadow.BlurRadius = idleBlur;
                letter.PaintedGlow = 0;
                letter.PaintedBlur = idleBlur;
            }

            // The halo is invisible now (opacity 0). Free the sprite and its mask
            // here instead of holding them until the line leaves the ±1 window.
            if (letter.GlowShown && letter.PaintedGlow == 0)
            {
                ReleaseGlow(letter, keepMask: true);
                letter.GlowDone = true;
            }
        }
    }

    /// <summary>Lazily gives a letter its Gaussian glow. The sprite lives in an empty
    /// layer before the text, so the shadow is drawn behind the sharp glyph.</summary>
    private void EnsureGlow(Letter letter)
    {
        if (!_glow || letter.GlowHost is null || letter.GlowVisual is not null)
        {
            return;
        }

        var width = letter.Text.ActualWidth;
        var height = letter.Text.ActualHeight;
        if (width < 0.5 || height < 0.5)
        {
            return; // the first paint can run before the letter has a size
        }

        var visual = ElementCompositionPreview.GetElementVisual(letter.GlowHost);
        var compositor = visual.Compositor;
        var shadow = compositor.CreateDropShadow();
        shadow.Color = SungColor;
        shadow.Offset = Vector3.Zero;
        shadow.Opacity = 0;
        shadow.BlurRadius = _glowBlurFixed ? 12 : 0;
        if (!_glowNoMask)
        {
            var source = _glowStaticMask && letter.MaskText is { } maskText
                ? maskText
                : letter.Text;
            if (letter.Mask is null || letter.NeedsMaskRefresh)
            {
                // Keep the static mask for the life of the live window. A new glow
                // after a seek reuses it instead of rasterizing the same glyph again.
                letter.Mask = MaskFor(source);
                letter.NeedsMaskRefresh = false;
            }

            shadow.Mask = letter.Mask;
        }

        var glowBrush = compositor.CreateColorBrush(Color.FromArgb(0, 0, 0, 0));
        var sprite = compositor.CreateSpriteVisual();
        sprite.Brush = glowBrush;
        sprite.Shadow = shadow;
        sprite.Size = new Vector2((float)width, (float)height);

        ElementCompositionPreview.SetElementChildVisual(letter.GlowHost, sprite);
        letter.Shadow = shadow;
        letter.GlowVisual = sprite;
        letter.GlowBrush = glowBrush;
        letter.PaintedGlow = 0;
        letter.PaintedBlur = _glowBlurFixed ? 12 : 0;
        letter.GlowDone = false;
        AddLiveGlow(letter);
    }

    private void RefreshGlowMask(Letter letter)
    {
        if (letter.Shadow is null || letter.GlowVisual is null)
        {
            return;
        }

        var width = letter.Text.ActualWidth;
        var height = letter.Text.ActualHeight;
        if (width < 0.5 || height < 0.5)
        {
            return;
        }

        letter.GlowVisual.Size = new Vector2((float)width, (float)height);
        if (_glowNoMask)
        {
            letter.NeedsMaskRefresh = false;
            return;
        }

        var source = _glowStaticMask && letter.MaskText is { } maskText
            ? maskText
            : letter.Text;
        var mask = MaskFor(source);
        if (!ReferenceEquals(letter.Mask, mask))
        {
            letter.Shadow.Mask = mask;
            // Do not dispose the replaced mask: GetAlphaMask may hand the same brush
            // back for the same element, and a closed brush was what broke the glow.
            letter.Mask = mask;
        }

        letter.NeedsMaskRefresh = false;
    }

    /// <summary>The static twin's alpha mask, shared by every glyph with the same shape.
    /// A seek across the song reuses these instead of rasterizing each letter again.</summary>
    private CompositionBrush MaskFor(TextBlock source)
    {
        if (!_glowStaticMask)
        {
            return source.GetAlphaMask();
        }

        var key = new MaskKey(
            source.Text,
            source.FontFamily.Source,
            source.FontSize,
            source.FontWeight.Weight,
            source.CharacterSpacing,
            source.LineHeight);
        if (!_glyphMasks.TryGetValue(key, out var mask))
        {
            // A huge alphabet must not turn the shared set into the new high-water
            // mark. Old brushes may still be in use by a shadow; the dictionary just
            // drops its reference and the shadow keeps it alive.
            if (_glyphMasks.Count >= 128 && _glyphMaskOrder.Count > 0)
            {
                _glyphMasks.Remove(_glyphMaskOrder.Dequeue());
            }

            mask = source.GetAlphaMask();
            _glyphMasks[key] = mask;
            _glyphMaskOrder.Enqueue(key);
        }

        return mask;
    }

    private void ClearGlyphMasks()
    {
        _glyphMasks.Clear();
        _glyphMaskOrder.Clear();
    }

    private static void ReleaseGlow(Letter letter, bool keepMask = false)
    {
        if (letter.GlowHost is { } host)
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
        }

        letter.GlowVisual?.Dispose();
        letter.GlowVisual = null;
        letter.GlowBrush?.Dispose();
        letter.GlowBrush = null;
        letter.Shadow?.Dispose();
        letter.Shadow = null;
        if (!keepMask)
        {
            // The letter is leaving the live window. Drop the mask reference and let
            // the GC release it; disposing here could close a brush the framework
            // returns again for the same element.
            letter.Mask = null;
        }

        letter.GlowShown = false;
        letter.PaintedGlow = -1;
        letter.PaintedBlur = -1;
    }

    private void ReleaseGlows()
    {
        _glowWarmup.Clear();
        _glowWarmupSet.Clear();
        _liveGlows.Clear();
        _liveGlowSet.Clear();
        _glowWindowIndex = -2;
        foreach (var row in _rows)
        {
            foreach (var word in row.WordItems)
            {
                foreach (var letter in word.Letters)
                {
                    letter.GlowWanted = false;
                    ReleaseGlow(letter);
                    letter.GlowDone = false;
                }
            }

            foreach (var word in row.BackgroundItems)
            {
                foreach (var letter in word.Letters)
                {
                    letter.GlowWanted = false;
                    ReleaseGlow(letter);
                    letter.GlowDone = false;
                }
            }
        }
    }

    private void AddLiveGlow(Letter letter)
    {
        if (_liveGlowSet.Add(letter))
        {
            _liveGlows.Add(letter);
        }
    }

    /// <summary>Re-arms the glow window after a backward seek without disposing the
    /// glows that are still in it. Only the letters whose glow ended (and were freed)
    /// need a new mask; the rest are reused.</summary>
    private void RearmGlows(int newIndex)
    {
        if (!_glow)
        {
            return;
        }

        if (newIndex < 0 || newIndex >= _rows.Count)
        {
            ReleaseGlows();
            return;
        }

        _glowWindowIndex = -2;
        foreach (var letter in _liveGlows)
        {
            letter.GlowDone = false;
        }
    }

    /// <summary>Keeps the shadow sprites only around the line being sung: the current
    /// line and the next one. Creating them for the whole song in one go costs hundreds
    /// of megabytes of alpha masks; keeping none makes each long note pay for a new one.
    /// Only the small live list is scanned when the line changes.</summary>
    private void ScheduleGlows(int index)
    {
        if (!_glow || index == _glowWindowIndex)
        {
            return;
        }

        _glowWindowIndex = index;
        var start = Math.Max(0, index - 1);
        var end = Math.Min(_rows.Count - 1, index + 1);

        for (var i = _liveGlows.Count - 1; i >= 0; i--)
        {
            var letter = _liveGlows[i];
            if (letter.RowIndex >= start && letter.RowIndex <= end)
            {
                continue;
            }

            letter.GlowWanted = false;
            _glowWarmupSet.Remove(letter);
            ReleaseGlow(letter);
            letter.GlowDone = false;
            _liveGlowSet.Remove(letter);
            _liveGlows.RemoveAt(i);
        }

        for (var rowIndex = start; rowIndex <= end; rowIndex++)
        {
            var row = _rows[rowIndex];
            foreach (var word in row.WordItems)
            {
                foreach (var letter in word.Letters)
                {
                    letter.GlowWanted = true;
                    AddLiveGlow(letter);
                    if (letter.GlowVisual is null && !letter.GlowDone && _glowWarmupSet.Add(letter))
                    {
                        _glowWarmup.Enqueue(letter);
                    }
                }
            }

            foreach (var word in row.BackgroundItems)
            {
                foreach (var letter in word.Letters)
                {
                    letter.GlowWanted = true;
                    AddLiveGlow(letter);
                    if (letter.GlowVisual is null && !letter.GlowDone && _glowWarmupSet.Add(letter))
                    {
                        _glowWarmup.Enqueue(letter);
                    }
                }
            }
        }
    }

    /// <summary>Builds at most one shadow mask per frame, so a line's glow is ready before
    /// it is sung without a composition setup spike on the frame a note starts.</summary>
    private void WarmQueuedGlows()
    {
        while (_glowWarmup.Count > 0)
        {
            var letter = _glowWarmup.Dequeue();
            _glowWarmupSet.Remove(letter);
            if (!letter.GlowWanted || letter.GlowVisual is not null || letter.GlowHost is null
                || letter.GlowDone)
            {
                continue;
            }

            if (letter.Text.ActualWidth < 0.5 || letter.Text.ActualHeight < 0.5)
            {
                continue;
            }

            EnsureGlow(letter);
            return;
        }
    }

    /// <summary>Diagnostic only: runs a full GC when a request file exists, then logs
    /// private bytes before and after. The request file is created by the test script.</summary>
    private void CheckGcProbe()
    {
        var request = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "koda-lyrics-gc-request");
        if (!System.IO.File.Exists(request))
        {
            return;
        }

        try
        {
            System.IO.File.Delete(request);
        }
        catch (Exception)
        {
            // try again next frame
            return;
        }

        var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();
        var before = process.PrivateMemorySize64;
        var managedBefore = GC.GetTotalMemory(false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        var after = process.PrivateMemorySize64;
        var managedAfter = GC.GetTotalMemory(true);

        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "koda-lyrics-gc.txt"),
                $"{DateTime.Now:HH:mm:ss.fff} private={before / 1048576.0:F1}MB -> " +
                $"{after / 1048576.0:F1}MB managed={managedBefore / 1048576.0:F1}MB -> " +
                $"{managedAfter / 1048576.0:F1}MB gc0={GC.CollectionCount(0)} " +
                $"gc2={GC.CollectionCount(2)}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // diagnostics must not take the panel down
        }
    }

    /// <summary>Diagnostic only: once a second, logs the live glow count, the letters
    /// painted this frame, and private-byte growth.</summary>
    private void LogLiveSet()
    {
        var now = _frameClock.ElapsedMilliseconds;
        if (now - _lastLiveLogMs < 1000)
        {
            return;
        }

        var elapsed = Math.Max(1, now - _lastLiveLogMs) / 1000.0;
        var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();
        var privateBytes = process.PrivateMemorySize64;
        var growth = _lastLivePrivateBytes == 0
            ? 0
            : (privateBytes - _lastLivePrivateBytes) / 1048576.0 / elapsed;
        var live = 0;
        foreach (var letter in _liveGlows)
        {
            if (letter.GlowVisual is not null)
            {
                live++;
            }
        }

        _lastLiveLogMs = now;
        _lastLivePrivateBytes = privateBytes;

        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "koda-lyrics-live.txt"),
                $"{DateTime.Now:HH:mm:ss.fff} test={_glowTest} live={live} painted={_paintedLetters} " +
                $"private={privateBytes / 1048576.0:F1}MB growth={growth:F1}MB/s{Environment.NewLine}");
        }
        catch (Exception)
        {
            // diagnostics must not take the panel down
        }
    }

    /// <summary>Diagnostic only: appends frame gaps over 40 ms with GC counts so a glow-on
    /// and glow-off run can be compared on the same song.</summary>
    private void LogFrameGap()
    {
        var now = _frameClock.ElapsedMilliseconds;
        var previous = _lastFrameLogMs;
        _lastFrameLogMs = now;
        if (previous == 0)
        {
            return;
        }

        var gap = now - previous;
        if (gap < 40)
        {
            return;
        }

        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "koda-lyrics-frames.txt"),
                $"{DateTime.Now:HH:mm:ss.fff} gap={gap}ms pos={_position:F2} " +
                $"glide={_tween.IsRunning} dots={_interludeActive} " +
                $"gc0={GC.CollectionCount(0)} gc2={GC.CollectionCount(2)}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Diagnostics must never take the panel down.
        }
    }

    /// <summary>A step toward a value, with the softness of a spring.</summary>
    private static double Approach(double current, double target, double factor = 0.18)
    {
        var step = (target - current) * factor;
        if (Math.Abs(step) < 0.001)
        {
            return target;
        }

        return current + step;
    }

    /// <summary>Places the line being sung at the reading anchor at once, for a fresh layout
    /// or a paused panel where a glide would be wasted or leave it off the anchor.</summary>
    private void JumpTo(Row row)
    {
        if (_scroll.ViewportHeight <= 1)
        {
            _needsGlide = true;
            return;
        }

        double target;
        try
        {
            var offset = row.Host.TransformToVisual(_lines)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            target = Math.Max(0, offset - _scroll.ViewportHeight * ReadingAnchor);
        }
        catch (Exception)
        {
            return;
        }

        _tween.Stop();
        _scroll.ChangeView(null, target, null, true);
    }

    /// <summary>The glide to the line being sung: a spring, never a jump.</summary>
    private void GlideTo(Row row)
    {
        if (_scroll.ViewportHeight <= 1)
        {
            // The panel has no height yet (it is coming into view): try again in a frame.
            _needsGlide = true;
            return;
        }

        double target;
        try
        {
            var offset = row.Host.TransformToVisual(_lines).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            target = Math.Max(0, offset - _scroll.ViewportHeight * ReadingAnchor);
        }
        catch (Exception)
        {
            return; // the panel is not on screen yet
        }

        var start = _scroll.VerticalOffset;
        if (Math.Abs(target - start) < 1)
        {
            return;
        }

        _tween.Stop();
        _tweenStart = start;
        _tweenTarget = target;
        _tweenElapsed = 0;
        _tween.Start();
    }

    private double _tweenStart;
    private double _tweenTarget;
    private int _tweenElapsed;

    private void OnTweenTick(object? sender, object args)
    {
        _tweenElapsed += 16;
        var k = Math.Min(1.0, _tweenElapsed / (double)GlideMilliseconds);
        var eased = EaseOut(k);
        _scroll.ChangeView(null, _tweenStart + (_tweenTarget - _tweenStart) * eased, null, true);
        if (k >= 1)
        {
            _tween.Stop();
        }
    }

    /// <summary>A smooth ease-out with no overshoot: the new line settles where it lands
    /// instead of dipping past it and coming back.</summary>
    private static double EaseOut(double k)
    {
        var x = 1 - k;
        return 1 - x * x * x;
    }

    private static SolidColorBrush Brush(byte alpha) =>
        new(Windows.UI.Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));

    /// <summary>The lyric type: the chosen family, the look's weight, a little tracked out and airy.
    /// The backing vocals are the same shape in a lighter weight.</summary>
    private void Shape(TextBlock block, double fontSize, bool soft = false)
    {
        block.FontFamily = _font;
        block.FontWeight = soft
            ? _renderer == LyricsRenderer.Expressive
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Medium
            : _renderer switch
            {
                LyricsRenderer.Classic => Microsoft.UI.Text.FontWeights.Normal,
                _ => Microsoft.UI.Text.FontWeights.Bold,
            };
        block.FontSize = fontSize;
        block.CharacterSpacing = _renderer == LyricsRenderer.Expressive ? 0 : Tracking;
        block.LineHeight = Math.Round(fontSize * _lineHeightFactor);
        block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
    }

    public void Dispose()
    {
        ReleaseGlows();
        ClearGlyphMasks();
        _hoverOpacityIn?.Dispose();
        _hoverOpacityOut?.Dispose();
        _hoverScaleIn?.Dispose();
        _hoverScaleOut?.Dispose();
        _hoverEase?.Dispose();
        _tween.Stop();
        _frame.Stop();
    }
}


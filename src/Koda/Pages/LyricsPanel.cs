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
using Microsoft.UI.Xaml.Documents;
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

    /// <summary>The two ends of a word's fill: the sung colour and the waiting one. They
    /// start dark, as Now Playing is, and are re-read from the panel's theme when it fills.</summary>
    private Color _sung = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private Color _unsung = Color.FromArgb(0x61, 0xFF, 0xFF, 0xFF);

    /// <summary>Spicy's colors: 85% white once sung, 50% white while waiting, in dark.</summary>
    private Color _expressiveSung = Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF);

    /// <summary>The static mask twin's fill: the reference alpha for the mask; the
    /// shadow's own opacity still scales the visible glow. It stays white in both
    /// themes because only its alpha is read.</summary>
    private readonly Color _maskTwinColor = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private Color _expressiveUnsung = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
    private Color _expressiveHoverSung = Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF);
    private Color _expressiveHoverUnsung = Color.FromArgb(0xBF, 0xFF, 0xFF, 0xFF);
    private Color _expressiveActiveHoverSung = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private Color _expressiveActiveHoverUnsung = Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF);
    private Color _hoverBoxColor = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);

    /// <summary>The theme the palette was read under, so a palette left over from the
    /// other theme is rebuilt when the panel is shown again.</summary>
    private ElementTheme _paletteTheme = ElementTheme.Dark;

    /// <summary>How long after a line's end its springs may keep painting.</summary>
    private const double ExpressiveTailSeconds = 2.6;

    /// <summary>How many lines around the one being sung keep their per-word text. Far
    /// lines keep one plain text block, which looks the same while unsung but allocates no
    /// text surface per word.</summary>
    private const int RichRowsBefore = 2;
    private const int RichRowsAfter = 4;
    private const int RichRowsRelease = 3;

    /// <summary>Disposed masks that may pile up before a mid-playback collection runs.
    /// The backlog is native surface memory that waits for a collection either way; a
    /// rare collection costs the glide far less than a frequent one.</summary>
    private const int MaskCollectionThreshold = 60;

    /// <summary>How far apart mid-playback collections must stay, on the frame clock.</summary>
    private const long MaskCollectionIntervalMs = 60_000;

    /// <summary>How far apart the layout-pass transform probes may run while the panel
    /// is otherwise unchanged. A glide arranges the panel every frame; the probe is for
    /// a size event that never arrived, and a second is soon enough to catch one.</summary>
    private const long LayoutProbeIntervalMs = 1_000;

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
    private double _spaceSize = -1;
    private double _spaceSoftSize = -1;
    private double _spaceWidth;
    private double _spaceSoftWidth;
    private BackingVocalsMode _backingMode = BackingVocalsMode.Shown;
    private double _idleNear = 0.5;
    private double _idleFar = 0.36;
    private bool _glow = true;
    private double _glowIntensity = 1;
    private bool _motion = true;
    private LyricsRenderer _renderer = LyricsRenderer.Sweep;
    private bool _compensateSpacing = true;
    private readonly bool _allRowsRich =
        Environment.GetEnvironmentVariable("KODA_NP_WINDOWED") != "1";
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
    private readonly List<Word> _plainWordPool = [];
    private readonly List<Word> _splitWordPool = [];
    private int _plainWordsMade;
    private int _splitWordsMade;
    private int _lettersMade;
    private int _glowWindowIndex = -2;
    private long _glowHoldUntil;
    private int _activeRowIndex = -1;
    private int _masksDisposed;
    private int _masksDisposedSinceCollection;
    private int _collectionsForced;
    private int _masksCreated;
    private long _lastMaskCollectionMs = -MaskCollectionIntervalMs;
    private long _lastLayoutProbeMs;
    private int _glowsBuilt;
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
    private Lyrics? _pendingLyrics;
    private bool _rowsBuilt;
    private int _collectionRunning;
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
        public LyricWord Data { get; set; }
        public required FrameworkElement Element { get; init; }
        /// <summary>The word's own text block, or null when it is split into letters.</summary>
        public TextBlock? Text { get; init; }
        /// <summary>The mask the word fills through, or null when each letter has its own.</summary>
        public LinearGradientBrush? Fill { get; init; }
        /// <summary>The bounce of the word.</summary>
        public required CompositeTransform Transform { get; init; }
        /// <summary>The composition visual the bounce is written to. Animating a
        /// composition transform keeps the text surface from being re-rasterized on
        /// every frame, which is what the old per-frame XAML RenderTransform did.</summary>
        public Visual? Visual { get; set; }
        /// <summary>The point the word scales around, as fractions of its size.</summary>
        public double OriginX { get; set; } = 0.5;
        public double OriginY { get; set; } = 0.5;
        /// <summary>The letters of a held syllable; empty for an ordinary word.</summary>
        public List<Letter> Letters { get; } = [];
        /// <summary>How many of <see cref="Letters"/> belong to the current word; the pool
        /// may keep extra hidden letters ready for a longer held syllable.</summary>
        public int LetterCount { get; set; }
        /// <summary>Whether this is a syllable of the word before it: the two may not be
        /// split by a line break.</summary>
        public bool JoinsPrevious { get; set; }
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
        /// <summary>The motion translation without the spacing correction.</summary>
        public double MotionX { get; set; }
        public double MotionY { get; set; }
        /// <summary>The visual shift that keeps word gaps proportional to its scale.</summary>
        public double SpaceX { get; set; }
        /// <summary>The row the word sits in, so a scale write can mark that row's spacing
        /// compensation dirty without the caller having to know the row.</summary>
        public Row? Owner { get; set; }
    }

    /// <summary>One letter of a held syllable: its own fill, wave and glow layers.</summary>
    private sealed class Letter
    {
        public required FrameworkElement Element { get; init; }
        public required TextBlock Text { get; init; }
        /// <summary>Test only: the never-changing twin the alpha mask is taken from.</summary>
        public TextBlock? MaskText { get; set; }
        /// <summary>The element in the glow host that holds the twin, for replacing it when
        /// its mask is disposed.</summary>
        public FrameworkElement? MaskWrapper { get; set; }
        /// <summary>The size and weight the twin was built at, so it can be rebuilt.</summary>
        public double MaskFontSize { get; set; }
        public bool MaskSoft { get; set; }
        public required LinearGradientBrush Fill { get; init; }
        public required CompositeTransform Transform { get; init; }
        /// <summary>The sharp letter's composition visual, for the wave.</summary>
        public Visual? Visual { get; set; }
        /// <summary>The glow layer's composition visual, moved with the letter unless a
        /// test mode keeps the glow still.</summary>
        public Visual? GlowHostVisual { get; set; }
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
        public int Index { get; set; }
        public int Count { get; set; }
        public double PaintedLit { get; set; } = double.NaN;
        public bool PaintedHovered { get; set; }
        public bool PaintedActive { get; set; }
        public double PaintedScale { get; set; } = -1;
        public double PaintedX { get; set; } = double.NaN;
        public double PaintedY { get; set; } = double.NaN;
        public double PaintedGlow { get; set; } = -1;
        public double PaintedBlur { get; set; } = -1;
        /// <summary>When the glow last fell to zero, in frame-clock milliseconds: a brief
        /// dip must not free the surfaces only to rebuild them a moment later.</summary>
        public long GlowIdleSince { get; set; } = -1;
        /// <summary>True once this letter's glow has actually been visible.</summary>
        public bool GlowShown { get; set; }
        /// <summary>True once the glow ended and its surfaces were freed.</summary>
        public bool GlowDone { get; set; }
    }

    /// <summary>One line in the panel: its words, its backing vocals and the line itself.</summary>
    private sealed class Row
    {
        public required LyricLine Line { get; init; }
        /// <summary>The line's place in the song, for its letters' glow window.</summary>
        public int Index { get; init; }
        /// <summary>The element that holds the line, for finding its place in the panel.</summary>
        public FrameworkElement Host { get; set; } = null!;
        /// <summary>The stack that holds the words and backing vocals.</summary>
        public StackPanel Content { get; set; } = null!;
        public required Panel Words { get; init; }
        public required WordWrapPanel Background { get; init; }
        public List<Word> WordItems { get; } = [];
        public List<Word> BackgroundItems { get; } = [];
        /// <summary>Whether the line has its word-level text (or is a cheap one-block line).</summary>
        public bool Rich { get; set; }
        /// <summary>The one-block stand-in a far line keeps while its words are released.</summary>
        public TextBlock? Placeholder { get; set; }
        public SolidColorBrush? PlaceholderBrush { get; set; }
        /// <summary>The one-block stand-in for backing vocals on far lines.</summary>
        public TextBlock? BackgroundPlaceholder { get; set; }
        public SolidColorBrush? BackgroundPlaceholderBrush { get; set; }
        /// <summary>The rest scale last written to the two placeholders.</summary>
        public double PlaceholderScale { get; set; } = -1;
        public double BackgroundPlaceholderScale { get; set; } = -1;

        /// <summary>The height the rich content had, so collapsing does not move the scroll.</summary>
        public double CachedHeight { get; set; }
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
        /// <summary>Whether a word's scale or the row's layout changed since its word gaps
        /// were last compensated. A resting row's shifts never change, so its compensation
        /// is computed once instead of on every frame.</summary>
        public bool SpacingDirty { get; set; } = true;
        /// <summary>The word placements the spacing compensation reuses, so its pass
        /// allocates nothing per frame.</summary>
        public List<PlacedWord>? Placed { get; set; }
    }

    /// <summary>One word's placement, as the spacing compensation reads it: the shift math
    /// runs per visual row of words, over a buffer the row keeps between frames.</summary>
    private readonly record struct PlacedWord(
        Word Word,
        double X,
        double Y,
        double Width,
        double MarginLeft,
        double MarginRight);

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
        // checks. The check is cheap, but the transform probe it asks for is not: a glide
        // scrolls the panel every frame and each frame's arrange raised it, so the probe
        // itself is throttled to a slow heartbeat. The strong signals (the size events,
        // the XamlRoot change, the shell's relayout) still probe at once.
        _scroll.LayoutUpdated += (_, _) => OnLayoutUpdated();
        // Now Playing is always dark; the inline drawer follows the app's theme.
        _scroll.ActualThemeChanged += OnActualThemeChanged;
    }

    /// <summary>A light/dark switch repaints the words with the other palette. The song
    /// and its timings stay; only the rows are rebuilt. A closed panel records the new
    /// palette the next time it opens.</summary>
    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_pendingLyrics is not null)
        {
            Load(_pendingLyrics);
            Follow();
        }
    }

    /// <summary>Re-reads the panel's size and reflows. Called when the window moved to
    /// another display or zoom, where the ScrollViewer's own SizeChanged can be missed.</summary>
    public void RefreshLayout()
    {
        // A suspended panel keeps its rows but is not on screen; nothing to reflow.
        if (!_visible)
        {
            return;
        }

        // A layout pass is the moment the ScrollViewer can report a stale slot after a
        // pane, display or zoom change, so this is the one place that pays for the
        // TransformToVisual probe.
        if (SyncSize(probe: true))
        {
            Paint();
        }
    }

    /// <summary>A layout pass ran, which the glide itself does every frame it scrolls.
    /// The size check is cheap and always runs; the transform probe behind it is not, so
    /// a missed size event is caught within a second rather than on every frame.</summary>
    private void OnLayoutUpdated()
    {
        if (!_visible)
        {
            return;
        }

        if (Math.Abs(_scroll.ActualWidth - _panelWidth) < 0.5
            && Math.Abs(_scroll.ActualHeight - _panelHeight) < 0.5)
        {
            // The size the panel already knows: nothing any pass can add. This is the
            // fast path every glide frame takes.
            return;
        }

        var now = _frameClock.ElapsedMilliseconds;
        if (now - _lastLayoutProbeMs < LayoutProbeIntervalMs)
        {
            return;
        }

        _lastLayoutProbeMs = now;
        RefreshLayout();
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

    /// <summary>Whether the rows are built and can be shown without a rebuild.</summary>
    public bool HasRows => _rowsBuilt;

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

    /// <summary>A host with a narrow gutter (the inline drawer, the expanded mini player)
    /// keeps only the hover box's reach instead of the 18/24 DIP glow pads Now Playing uses.
    /// The host supplies the rest of the room with a margin or a bleed.</summary>
    public bool TightInsets { get; set; }

    /// <summary>How far the hover box's outer edge sits from the pane's edge in a tight
    /// host: the drawer's header padding, or the mini card's padding. The line inset becomes
    /// this minus the ScrollViewer margin (the scrollbar inset) plus the hover box's reach,
    /// so duet lines get the same gutter on both sides.</summary>
    public double Gutter { get; set; }

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
            if (value)
            {
                // The listener has taken the panel: let go of the glide at once, rather
                // than fighting their scroll for the rest of its half second.
                _tween.Stop();
                _needsGlide = false;
            }

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
            // The listener may have scrolled far from the active line while the rich
            // window stayed behind; materialize the lines around the song again.
            EnsureRichWindow(Lyrics.IndexOf(_position, _lyricLines) ?? 0);
            GlideTo(_current);
        }
    }

    /// <summary>The song's place in time, with the listener's correction and the lead.</summary>
    public void SetPosition(double positionSeconds)
    {
        var next = positionSeconds + Lead - _offset;
        if (_liveLog && next < _position && _position - next < 1.0
            && _position - next >= 0.001)
        {
            // A small backward step may be the position source jittering rather than a
            // seek. Record it without touching the jump thresholds.
            LogBackwardStep(_position - next);
        }

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

            // A drag or a skip moves the position in bursts. Hold the glow rebuilds for
            // a moment after the jump, so the masks are built once for where the seek
            // landed instead of once for every intermediate line.
            _glowHoldUntil = _frameClock.ElapsedMilliseconds + 300;
            _glowWindowIndex = -2;
        }

        _position = next;
    }

    /// <summary>The words are white on Now Playing's dark surface and near-black in the
    /// light theme's inline drawer. The mask twin stays opaque white in both themes,
    /// because the glow reads only its alpha.</summary>
    private void ReadPalette()
    {
        _paletteTheme = _scroll.ActualTheme;
        if (_scroll.ActualTheme == ElementTheme.Light)
        {
            _sung = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
            _unsung = Color.FromArgb(0x61, 0x00, 0x00, 0x00);
            _expressiveSung = Color.FromArgb(0xD9, 0x00, 0x00, 0x00);
            _expressiveUnsung = Color.FromArgb(0x80, 0x00, 0x00, 0x00);
            _expressiveHoverSung = Color.FromArgb(0xD9, 0x00, 0x00, 0x00);
            _expressiveHoverUnsung = Color.FromArgb(0xBF, 0x00, 0x00, 0x00);
            _expressiveActiveHoverSung = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
            _expressiveActiveHoverUnsung = Color.FromArgb(0xF2, 0x00, 0x00, 0x00);
            _hoverBoxColor = Color.FromArgb(0x1A, 0x00, 0x00, 0x00);
            return;
        }

        _sung = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        _unsung = Color.FromArgb(0x61, 0xFF, 0xFF, 0xFF);
        _expressiveSung = Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF);
        _expressiveUnsung = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
        _expressiveHoverSung = Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF);
        _expressiveHoverUnsung = Color.FromArgb(0xBF, 0xFF, 0xFF, 0xFF);
        _expressiveActiveHoverSung = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        _expressiveActiveHoverUnsung = Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF);
        _hoverBoxColor = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);
    }

    /// <summary>The appearance settings, read once per fill so a song never changes look
    /// halfway through.</summary>
    private void ReadLook()
    {
        ReadPalette();
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
        _compensateSpacing = settings.LyricsCompensateSpacing
            && Environment.GetEnvironmentVariable("KODA_NP_NO_SPACING_COMP") != "1";
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
        ReturnAllRowWords();
        _repaintFrom = -1;
        _repaintTo = -1;
        _lines.Children.Clear();
        _rows.Clear();
        _lyricLines.Clear();
        _leadingDots = null;
        _leadingDotItems = [];
        _current = null;
        _rowsBuilt = false;
        _loaded = lyrics is { IsSynced: true };
        _pendingLyrics = lyrics;

        if (lyrics is null)
        {
            // A clear is not a song change: the hosts empty the panel while the next
            // song's words are still on their way, and the real load that follows
            // collects once, after the old song's surfaces are already dropped. A
            // collection here fired twice per song change for nothing.
            return;
        }

        // The previous song's rows, words, placeholders and masks were just dropped. Collect
        // after those references are gone, so the new song does not allocate on top of the
        // old song's native text and composition surfaces.
        ForceCollection();

        if (lyrics.IsSynced)
        {
            _lyricLines.AddRange(lyrics.Lines);
        }

        // A closed lyrics panel keeps the timings (the current line and the timing menu
        // still read them) but does not build the song's text. A skip while the panel is
        // closed must not allocate hundreds of native text surfaces it will never show.
        if (!_visible)
        {
            return;
        }

        BuildRows();
    }

    /// <summary>Builds the row hosts and the initial word-level window for the stored
    /// lyrics. Only the lines near the one being sung materialize their words and glow
    /// layers; the rest keep one plain text block placeholder.</summary>
    private void BuildRows()
    {
        if (_pendingLyrics is not { } lyrics)
        {
            return;
        }

        _lines.Children.Clear();
        _rows.Clear();
        _leadingDots = null;
        _leadingDotItems = [];
        _tween.Stop();
        _needsGlide = false;

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
            if (MakeCredits(lyrics) is { } credits)
            {
                _lines.Children.Add(credits);
            }

            _rowsBuilt = true;
            UpdateSpacers();
            return;
        }

        if (_lyricLines.Count != lyrics.Lines.Count)
        {
            _lyricLines.Clear();
            _lyricLines.AddRange(lyrics.Lines);
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
                Index = index,
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
            row.Content = content;
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
            var textSide = rightAligned ? TextAlignment.Right : TextAlignment.Left;

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
                // Line-timed lyrics: one text block per line is cheap, and it lights as a
                // whole when the song reaches it.
                var lineText = new TextBlock
                {
                    Text = line.Text.Length == 0 ? "♪" : line.Text,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = textSide,
                    Foreground = Brush(0xFF),
                };
                Shape(lineText, fontSize);
                row.Words.Children.Add(lineText);
                row.Rich = true;
            }
            else if (!_allRowsRich)
            {
                // The stand-in is the line in the same type, dimmed like unsung words. It
                // keeps the row's place and stays readable without a text surface per word.
                var placeholder = MakeRowPlaceholder(line, fontSize, textSide);
                row.Placeholder = placeholder;
                row.PlaceholderBrush = placeholder.Foreground as SolidColorBrush;
                placeholder.SizeChanged += (_, _) =>
                {
                    if (row.PlaceholderScale > 0)
                    {
                        ApplyPlaceholderScale(placeholder, row.PlaceholderScale);
                    }
                };
                row.Words.Children.Add(placeholder);
                row.Rich = false;
            }

            _lines.Children.Add(host);
            _rows.Add(row);

            if (_allRowsRich && line.Words.Count > 0)
            {
                BuildRichRow(row);
            }

            if (_renderer == LyricsRenderer.Expressive && _motion && !row.Rich)
            {
                SetPlaceholderRest(row, _position >= line.Time ? 1 : LyricsMotion.WordRestScale);
            }

            var background = _allRowsRich && line.Words.Count > 0
                ? null
                : MakeBackgroundPlaceholder(line, fontSize, textSide);
            if (background is not null)
            {
                row.BackgroundPlaceholder = background;
                row.BackgroundPlaceholderBrush = background.Foreground as SolidColorBrush;
                background.SizeChanged += (_, _) =>
                {
                    if (row.BackgroundPlaceholderScale > 0)
                    {
                        ApplyPlaceholderScale(background, row.BackgroundPlaceholderScale);
                    }
                };
                row.Background.Children.Add(background);
                row.Content.Children.Add(row.Background);
                if (_renderer == LyricsRenderer.Expressive && _motion && !row.Rich)
                {
                    SetPlaceholderRest(row, _position >= line.Time ? 1 : LyricsMotion.WordRestScale);
                }
            }

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

        // The provider's credit sits with the words, after the last line.
        if (MakeCredits(lyrics) is { } attribution)
        {
            _lines.Children.Add(attribution);
        }

        // The room below the last line, so the final lines of a song can still be scrolled
        // up to the reading position.
        _lines.Children.Add(_spacer);

        // A song opened part way through needs its earlier lines painted as complete and
        // its later lines left unsung, not everything starting grey.
        _needsFullPaint = true;
        _rowsBuilt = true;
        ApplyTextWidth();
        UpdateSpacers();

        var start = _lyricLines.Count > 0
            ? (Lyrics.IndexOf(_position, _lyricLines) ?? 0)
            : 0;
        start = Math.Clamp(start, 0, Math.Max(0, _rows.Count - 1));
        EnsureRichWindow(start);
        if (!_allRowsRich)
        {
            // A windowed panel's rows collapse back into the pool as the song moves;
            // the pool must not keep every word of the biggest song for its life.
            TrimPools(400, 60);
        }
    }

    /// <summary>The provider and people to credit, as the last thing with the words.
    /// Spicy's community syncs and Unison require it; other sources have no credit.</summary>
    private TextBlock? MakeCredits(Lyrics lyrics)
    {
        if (lyrics.Credits.Count == 0)
        {
            return null;
        }

        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontSize = 12,
            Margin = new Thickness(0, 28, 0, 8),
            Foreground = Brush(0x99),
        };

        for (var index = 0; index < lyrics.Credits.Count; index++)
        {
            var credit = lyrics.Credits[index];
            if (index > 0)
            {
                block.Inlines.Add(new Run { Text = " · " });
            }

            if (credit.Role.Length > 0)
            {
                block.Inlines.Add(new Run { Text = credit.Role + " " });
            }

            if (!string.IsNullOrWhiteSpace(credit.Url) &&
                Uri.TryCreate(credit.Url, UriKind.Absolute, out var uri))
            {
                var link = new Hyperlink { NavigateUri = uri };
                link.Inlines.Add(new Run { Text = credit.Name });
                block.Inlines.Add(link);
            }
            else
            {
                block.Inlines.Add(new Run { Text = credit.Name });
            }
        }

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            block, string.Join(". ", lyrics.Credits.Select(credit => $"{credit.Role} {credit.Name}")));
        return block;
    }

    private TextBlock MakeRowPlaceholder(LyricLine line, double fontSize, TextAlignment textSide)
    {
        var brush = new SolidColorBrush(
            _renderer == LyricsRenderer.Expressive ? _expressiveUnsung : _unsung);
        var placeholder = new TextBlock
        {
            Text = line.Text.Length == 0 ? "♪" : line.Text,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = textSide,
            Foreground = brush,
        };
        Shape(placeholder, fontSize);
        return placeholder;
    }

    private TextBlock? MakeBackgroundPlaceholder(LyricLine line, double fontSize, TextAlignment textSide)
    {
        if (_backingMode == BackingVocalsMode.Hidden || line.Background.Count == 0)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var word in line.Background)
        {
            var text = word.Text;
            if (_backingMode == BackingVocalsMode.WithoutParentheses)
            {
                text = text.Replace("(", string.Empty).Replace(")", string.Empty).Trim();
                if (text.Length == 0)
                {
                    continue;
                }
            }

            parts.Add(text);
        }

        if (parts.Count == 0)
        {
            return null;
        }

        var brush = new SolidColorBrush(
            _renderer == LyricsRenderer.Expressive ? _expressiveUnsung : _unsung);
        var placeholder = new TextBlock
        {
            Text = string.Join(" ", parts),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = textSide,
            Foreground = brush,
        };
        Shape(placeholder, fontSize * 0.7, soft: true);
        return placeholder;
    }

    /// <summary>Materializes one word-level line: its words, gradient fills and glow
    /// layers. The line is built only while it is near the line being sung.</summary>
    private void BuildRichRow(Row row)
    {
        if (row.Rich || row.Line.Words.Count == 0)
        {
            return;
        }

        if (row.Words is not WordWrapPanel wrap)
        {
            row.Rich = true;
            return;
        }

        if (row.Placeholder is { } placeholder)
        {
            row.Words.Children.Remove(placeholder);
        }

        wrap.ClearWords();
        row.Background.ClearWords();
        row.WordItems.Clear();
        row.BackgroundItems.Clear();

        var line = row.Line;
        var fontSize = _fontSize;
        for (var wordIndex = 0; wordIndex < line.Words.Count; wordIndex++)
        {
            var item = TakeWord(
                line.Words[wordIndex],
                fontSize,
                soft: false,
                joinsPrevious: JoinsPrevious(line, wordIndex));
            item.Owner = row;
            row.WordItems.Add(item);
            wrap.Add(item.Element, item.JoinsPrevious);
        }

        // A word split into syllables bounces from its inner seam rather than its centre,
        // so the pieces stay attached while the word bounces.
        ShapeSyllables(row.WordItems);

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
            var item = TakeWord(spoken, fontSize * 0.7, soft: true, joinsPrevious: false);
            item.Owner = row;
            row.BackgroundItems.Add(item);
            row.Background.Add(item.Element, joinsPrevious: false);
        }

        row.Content.Children.Remove(row.Background);
        if (row.BackgroundItems.Count > 0)
        {
            ShapeSyllables(row.BackgroundItems);
            row.Content.Children.Add(row.Background);
        }

        foreach (var word in row.WordItems)
        {
            foreach (var letter in word.Letters)
            {
                letter.RowIndex = row.Index;
            }
        }

        foreach (var word in row.BackgroundItems)
        {
            foreach (var letter in word.Letters)
            {
                letter.RowIndex = row.Index;
            }
        }

        // The first render must already have the promoted row's spacing, not one frame
        // with the layout gaps still at full size.
        wrap.RefreshPlacements();
        row.Background.RefreshPlacements();
        if (_renderer == LyricsRenderer.Expressive && _motion && _compensateSpacing)
        {
            CompensateSpacing(row.WordItems, wrap, row);
            CompensateSpacing(row.BackgroundItems, row.Background, row);
            row.SpacingDirty = false;
        }

        row.Rich = true;
        row.CachedHeight = 0;
    }

    /// <summary>Releases a far line's word-level text surfaces and leaves the one-block
    /// placeholder at the height the rich row had, so the scroll does not jump.</summary>
    private void CollapseRichRow(Row row)
    {
        if (!row.Rich || row.Line.Words.Count == 0 || row.Hovered)
        {
            return;
        }

        var height = row.Words.ActualHeight;
        if (height > 0)
        {
            row.CachedHeight = height;
        }

        ReturnRowWords(row);
        if (row.Words is WordWrapPanel wrap)
        {
            wrap.ClearWords();
        }
        else
        {
            row.Words.Children.Clear();
        }
        row.Background.ClearWords();
        row.Content.Children.Remove(row.Background);
        row.WordItems.Clear();
        row.BackgroundItems.Clear();

        if (row.Placeholder is { } placeholder)
        {
            if (row.CachedHeight > 0)
            {
                placeholder.Height = row.CachedHeight;
            }
            else
            {
                placeholder.ClearValue(FrameworkElement.HeightProperty);
            }

            row.Words.Children.Add(placeholder);
        }

        if (row.BackgroundPlaceholder is { } background)
        {
            row.Background.Children.Add(background);
            row.Content.Children.Add(row.Background);
        }

        row.Rich = false;
    }

    private void ReleaseWordGlows(Word word)
    {
        foreach (var letter in word.Letters)
        {
            letter.GlowWanted = false;
            _glowWarmupSet.Remove(letter);
            ReleaseGlow(letter);
            letter.GlowDone = false;
            _liveGlowSet.Remove(letter);
            _liveGlows.Remove(letter);
        }
    }

    /// <summary>Keeps only the lines around the one being sung at word-level detail. The
    /// rest are one plain text block, which looks the same while unsung and costs a
    /// fraction of the native text surfaces.</summary>
    private void EnsureRichWindow(int index)
    {
        if (_allRowsRich)
        {
            return;
        }

        if (!_rowsBuilt || _rows.Count == 0 || _userScrolled)
        {
            return;
        }

        if (index < 0)
        {
            index = 0;
        }
        else if (index >= _rows.Count)
        {
            index = _rows.Count - 1;
        }

        var from = Math.Max(0, index - RichRowsBefore);
        var to = Math.Min(_rows.Count - 1, index + RichRowsAfter);

        // Release the old window first: its word elements go straight back to the pool
        // for the incoming lines instead of making a new set every line.
        var releaseFrom = Math.Max(0, index - RichRowsBefore - RichRowsRelease);
        var releaseTo = Math.Min(_rows.Count - 1, index + RichRowsAfter + RichRowsRelease);
        for (var i = 0; i < releaseFrom; i++)
        {
            CollapseRichRow(_rows[i]);
        }

        for (var i = releaseTo + 1; i < _rows.Count; i++)
        {
            CollapseRichRow(_rows[i]);
        }

        for (var i = from; i <= to; i++)
        {
            BuildRichRow(_rows[i]);
        }
    }

    /// <summary>Which side of the panel a line belongs to. A duet's second singer sits on
    /// the right; everyone else, including a group singing together, sits on the left.</summary>
    private static HorizontalAlignment SideOf(LyricLine line) =>
        line.Voice == LyricVoice.Duet ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    /// <summary>The rounded, translucent box that grows in behind a line under the pointer.
    /// It sits in its own layer, so it never changes the line's measure.</summary>
    private Border MakeHoverBox(double fontSize)
    {
        var box = new Border
        {
            Background = new SolidColorBrush(_hoverBoxColor),
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
    /// from its right edge, the last from its left, and the middle stays centred, as the
    /// Spicy CSS does.</summary>
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
                var originX = count == 1
                    ? 0.5
                    : index == start
                        ? 1
                        : index == end
                            ? 0
                            : 0.5;
                word.OriginX = originX;
                word.OriginY = 0.5;
                word.Element.RenderTransformOrigin = new Windows.Foundation.Point(originX, 0.5);
                ApplyCenter(word.Visual, word.Element, originX, 0.5);
            }

            start = end + 1;
        }
    }

    /// <summary>Three dots that fill in turn through an instrumental gap; they grow with
    /// the words so the pause reads at any size. They sit in the flow at half opacity even
    /// when their gap is not playing, so the interlude is already placed.</summary>
    private StackPanel MakeDots(List<Ellipse> dots, double fontSize, double verticalMargin)
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

    /// <summary>Takes a word-level element from the pool and points it at the new line's
    /// word. Reusing the TextBlocks is what stops one new native text surface per line.</summary>
    private Word TakeWord(LyricWord data, double fontSize, bool soft, bool joinsPrevious)
    {
        var pieces = _renderer == LyricsRenderer.Expressive && _motion && LyricsMotion.IsHeldSyllable(data)
            ? LyricsMotion.WaveLetters(data.Text)
            : null;
        if (pieces is { Count: > 0 })
        {
            return TakeSplitWord(data, fontSize, soft, joinsPrevious, pieces);
        }

        return TakePlainWord(data, fontSize, soft, joinsPrevious);
    }

    private Word TakePlainWord(LyricWord data, double fontSize, bool soft, bool joinsPrevious)
    {
        Word? word = null;
        if (_plainWordPool.Count > 0)
        {
            var last = _plainWordPool.Count - 1;
            word = _plainWordPool[last];
            _plainWordPool.RemoveAt(last);
        }

        if (word is null)
        {
            var fill = NewFill();
            var transform = new CompositeTransform();
            var text = new TextBlock
            {
                Foreground = fill,
                RenderTransform = transform,
                TextWrapping = TextWrapping.NoWrap,
            };
            ElementCompositionPreview.SetIsTranslationEnabled(text, true);
            word = new Word
            {
                Data = data,
                Element = text,
                Text = text,
                Fill = fill,
                Transform = transform,
                Visual = ElementCompositionPreview.GetElementVisual(text),
                FontSize = fontSize,
                JoinsPrevious = joinsPrevious,
            };
            _plainWordsMade++;
            TrackCenter(word);
        }

        word.Data = data;
        word.FontSize = fontSize;
        word.JoinsPrevious = joinsPrevious;
        word.LetterCount = 0;
        word.Element.Visibility = Visibility.Visible;
        word.Element.Margin = new Thickness(0, 0, data.SpaceAfter ? WordGap(fontSize, soft) : 0, 0);
        var block = word.Text!;
        block.Text = data.Text;
        block.Visibility = Visibility.Visible;
        Shape(block, fontSize, soft);
        ResetWordMotion(word);
        ResetFill(word.Fill!);
        return word;
    }

    private Word TakeSplitWord(
        LyricWord data,
        double fontSize,
        bool soft,
        bool joinsPrevious,
        IReadOnlyList<string> pieces)
    {
        Word? word = null;
        for (var i = 0; i < _splitWordPool.Count; i++)
        {
            var candidate = _splitWordPool[i];
            if (candidate.Letters.Count >= pieces.Count)
            {
                word = candidate;
                _splitWordPool.RemoveAt(i);
                break;
            }
        }

        word ??= CreateSplitWord(data, pieces.Count, fontSize, soft, joinsPrevious);

        word.Data = data;
        word.FontSize = fontSize;
        word.JoinsPrevious = joinsPrevious;
        word.LetterCount = pieces.Count;
        word.Element.Visibility = Visibility.Visible;
        var panel = (StackPanel)word.Element;
        panel.Margin = new Thickness(0, 0, data.SpaceAfter ? WordGap(fontSize, soft) : 0, 0);

        for (var i = 0; i < word.Letters.Count; i++)
        {
            var letter = word.Letters[i];
            if (i >= pieces.Count)
            {
                letter.Element.Visibility = Visibility.Collapsed;
                continue;
            }

            ConfigureLetter(letter, pieces[i], fontSize, soft, i, pieces.Count);
        }

        AlignLetters(word, data.Text, fontSize, soft);
        ResetWordMotion(word);
        return word;
    }

    private Word CreateSplitWord(
        LyricWord data,
        int count,
        double fontSize,
        bool soft,
        bool joinsPrevious)
    {
        var transform = new CompositeTransform();
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            RenderTransform = transform,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
        };
        ElementCompositionPreview.SetIsTranslationEnabled(panel, true);
        var word = new Word
        {
            Data = data,
            Element = panel,
            Text = null,
            Fill = null,
            Transform = transform,
            Visual = ElementCompositionPreview.GetElementVisual(panel),
            FontSize = fontSize,
            JoinsPrevious = joinsPrevious,
            LetterCount = count,
        };
        _splitWordsMade++;
        TrackCenter(word);
        for (var index = 0; index < count; index++)
        {
            var letter = MakeLetter(string.Empty, fontSize, soft, index, count);
            word.Letters.Add(letter);
            panel.Children.Add(letter.Element);
        }

        return word;
    }

    private void ConfigureLetter(Letter letter, string text, double fontSize, bool soft, int index, int count)
    {
        letter.Element.Visibility = Visibility.Visible;
        letter.Element.Margin = new Thickness(0);
        letter.Text.Text = text;
        Shape(letter.Text, fontSize, soft);
        if (letter.MaskText is { } twin)
        {
            twin.Text = text;
            Shape(twin, fontSize, soft);
        }

        // Remembered even when the twin is absent: the twin is built lazily when the
        // glow is wanted again, and it needs the size and weight the letter has now.
        letter.MaskFontSize = fontSize;
        letter.MaskSoft = soft;

        letter.Index = index;
        letter.Count = count;
        letter.Transform.ScaleX = 1;
        letter.Transform.ScaleY = 1;
        letter.Transform.TranslateX = 0;
        letter.Transform.TranslateY = 0;
        if (letter.GlowTransform is { } glowTransform)
        {
            glowTransform.ScaleX = 1;
            glowTransform.ScaleY = 1;
            glowTransform.TranslateX = 0;
            glowTransform.TranslateY = 0;
        }

        var rest = _renderer == LyricsRenderer.Expressive && _motion
            ? new LetterMotion(LyricsMotion.LetterRestScale, 0, LyricsMotion.LetterRestY, 0)
            : new LetterMotion(1, 0, 0, 0);
        var restY = rest.Y * fontSize;
        ApplyScale(letter.Visual, letter.Text, rest.Scale, 0.5, 0.5);
        ApplyTranslation(letter.Visual, 0, restY);
        ApplyScale(letter.GlowHostVisual, letter.GlowHost, rest.Scale, 0.5, 0.5);
        ApplyTranslation(letter.GlowHostVisual, 0, restY);

        letter.PaintedLit = double.NaN;
        letter.PaintedHovered = false;
        letter.PaintedActive = false;
        letter.PaintedScale = rest.Scale;
        letter.PaintedX = 0;
        letter.PaintedY = restY;
        letter.PaintedGlow = -1;
        letter.PaintedBlur = -1;
        letter.GlowShown = false;
        letter.GlowDone = false;
        letter.GlowIdleSince = -1;
        letter.GlowWanted = false;
        letter.NeedsMaskRefresh = true;
        ResetFill(letter.Fill);
    }

    /// <summary>Separate letter blocks do not shape/kerning together, so their sum can be
    /// wider than the word drawn as one run. Distribute the difference as static gaps between
    /// letters; the word then occupies exactly the placeholder's width at rest and after the
    /// wave. The gaps are layout, so no word is moved at runtime.</summary>
    private void AlignLetters(Word word, string text, double fontSize, bool soft)
    {
        var count = Math.Min(word.LetterCount, word.Letters.Count);
        if (count <= 0)
        {
            return;
        }

        if (count == 1)
        {
            word.Letters[0].Element.Margin = new Thickness(0);
            return;
        }

        var probe = new TextBlock { Text = text, TextWrapping = TextWrapping.NoWrap };
        Shape(probe, fontSize, soft);
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

        var total = 0.0;
        for (var i = 0; i < count; i++)
        {
            var element = word.Letters[i].Element;
            element.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            total += element.DesiredSize.Width;
        }

        var gap = (probe.DesiredSize.Width - total) / (count - 1);
        for (var i = 0; i < word.Letters.Count; i++)
        {
            word.Letters[i].Element.Margin = i > 0 && i < count
                ? new Thickness(gap, 0, 0, 0)
                : new Thickness(0);
        }
    }

    private void ResetWordMotion(Word word)
    {
        word.Transform.ScaleX = 1;
        word.Transform.ScaleY = 1;
        word.Transform.TranslateX = 0;
        word.Transform.TranslateY = 0;

        // An unsung Expressive word rests small and low; the spacing compensation keeps
        // the layout margins matching the scaled glyphs.
        var rest = _renderer == LyricsRenderer.Expressive && _motion
            ? new WordMotion(LyricsMotion.WordRestScale, 0, LyricsMotion.WordRestY)
            : new WordMotion(1, 0, 0);
        var y = rest.Y * word.FontSize;
        ApplyScale(word.Visual, word.Element, rest.Scale, word.OriginX, word.OriginY);
        ApplyTranslation(word.Visual, 0, y);

        word.MotionX = 0;
        word.MotionY = y;
        word.SpaceX = 0;
        word.PushX = 0;
        word.PaintedFill = -1;
        word.PaintedLit = double.NaN;
        word.PaintedHovered = false;
        word.PaintedActive = false;
        word.PaintedRise = -1;
        word.PaintedSwell = -1;
        word.PaintedScale = rest.Scale;
        word.PaintedX = 0;
        word.PaintedY = y;
    }

    /// <summary>Keeps a far row's placeholder at the Expressive rest size without changing
    /// its layout height, so promoting it to word-level text is only a change of detail.</summary>
    private void SetPlaceholderRest(Row row, double scale)
    {
        if (row.Placeholder is { } placeholder
            && Math.Abs(scale - row.PlaceholderScale) >= 0.001)
        {
            row.PlaceholderScale = scale;
            ApplyPlaceholderScale(placeholder, scale);
        }

        if (row.BackgroundPlaceholder is { } background
            && Math.Abs(scale - row.BackgroundPlaceholderScale) >= 0.001)
        {
            row.BackgroundPlaceholderScale = scale;
            ApplyPlaceholderScale(background, scale);
        }
    }

    private static void ApplyPlaceholderScale(FrameworkElement element, double scale)
    {
        var originX = element is TextBlock { TextAlignment: TextAlignment.Right } ? 1.0 : 0.0;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3(
            (float)(element.ActualWidth * originX),
            (float)(element.ActualHeight / 2),
            0);
        visual.Scale = new Vector3((float)scale, (float)scale, 1);
    }

    private void ResetFill(LinearGradientBrush fill)
    {
        var unsung = _renderer == LyricsRenderer.Expressive ? _expressiveUnsung : _unsung;
        var stops = fill.GradientStops;
        stops[0].Color = unsung;
        stops[1].Color = unsung;
        stops[2].Color = unsung;
        stops[1].Offset = 0;
        stops[2].Offset = 0.0001;
    }

    private void ReturnWordToPool(Word word)
    {
        ReleaseWordGlows(word);
        if (word.Element.Parent is Panel parent)
        {
            parent.Children.Remove(word.Element);
        }

        word.Element.Visibility = Visibility.Collapsed;
        if (word.Text is null)
        {
            _splitWordPool.Add(word);
        }
        else
        {
            _plainWordPool.Add(word);
        }
    }

    private void ReturnRowWords(Row row)
    {
        foreach (var word in row.WordItems)
        {
            ReturnWordToPool(word);
        }

        foreach (var word in row.BackgroundItems)
        {
            ReturnWordToPool(word);
        }

        _glowWarmup.Clear();
        _glowWarmupSet.Clear();
        _glowWindowIndex = -2;
    }

    private void ReturnAllRowWords()
    {
        foreach (var row in _rows)
        {
            ReturnRowWords(row);
        }
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
        _lettersMade++;
        ElementCompositionPreview.SetIsTranslationEnabled(textBlock, true);

        var host = new Grid();
        Grid? glowHost = null;
        CompositeTransform? glowTransform = null;
        TextBlock? maskText = null;
        FrameworkElement? maskWrapper = null;

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
            ElementCompositionPreview.SetIsTranslationEnabled(glowHost, true);
            host.Children.Add(glowHost);

            if (_glowStaticMask)
            {
                // Test only: a twin whose fill is a fixed solid, so the mask source is
                // never invalidated by the sharp letter's per-frame gradient. It is
                // nearly transparent but still rendered, which GetAlphaMask needs.
                (maskText, maskWrapper) = MakeMaskTwin(text, fontSize, soft);
                glowHost.Children.Add(maskWrapper);
            }
        }

        host.Children.Add(textBlock);
        host.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

        var letter = new Letter
        {
            Element = host,
            Text = textBlock,
            MaskText = maskText,
            MaskWrapper = maskWrapper,
            MaskFontSize = fontSize,
            MaskSoft = soft,
            Fill = fill,
            Transform = transform,
            Visual = ElementCompositionPreview.GetElementVisual(textBlock),
            GlowHostVisual = glowHost is null
                ? null
                : ElementCompositionPreview.GetElementVisual(glowHost),
            GlowHost = glowHost,
            GlowTransform = glowTransform,
            Index = index,
            Count = count,
        };
        TrackCenter(letter, textBlock, letter.Visual);
        if (glowHost is not null)
        {
            TrackCenter(letter, glowHost, letter.GlowHostVisual);
        }

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

    /// <summary>Builds a letter's static mask twin, with the wrapper that hides it in the
    /// glow host. Kept apart from <see cref="MakeLetter"/> so a disposed mask can be
    /// paired with a fresh twin.</summary>
    private (TextBlock Text, FrameworkElement Wrapper) MakeMaskTwin(string text, double fontSize, bool soft)
    {
        var maskText = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(_maskTwinColor),
            IsHitTestVisible = false,
            Opacity = _maskHide.Length > 0 ? 1 : _maskTwinOpacity,
            TextWrapping = TextWrapping.NoWrap,
        };
        Shape(maskText, fontSize, soft);

        if (_maskHide.Length == 0)
        {
            return (maskText, maskText);
        }

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

        return (maskText, wrapper);
    }

    private LinearGradientBrush NewFill() => new()
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(1, 0),
        GradientStops =
        {
            new GradientStop { Color = _unsung, Offset = 0 },
            new GradientStop { Color = _unsung, Offset = 0 },
            new GradientStop { Color = _unsung, Offset = 0.0001 },
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
        var (sidePad, endPad) = TextInsets();
        _lines.Margin = new Thickness(sidePad, 0, endPad, 0);
        var lineWidth = Math.Max(0, _panelWidth - sidePad - endPad);
        foreach (var row in _rows)
        {
            // Every word's size and placement just changed: the row's word gaps are
            // compensated again on the next pass.
            row.SpacingDirty = true;
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

            if (row.Placeholder is { } placeholder)
            {
                Shape(placeholder, _fontSize);
                placeholder.MaxWidth = lineWidth;
                placeholder.ClearValue(FrameworkElement.HeightProperty);
                row.CachedHeight = 0;
            }

            if (row.BackgroundPlaceholder is { } background)
            {
                Shape(background, _fontSize * 0.7, soft: true);
                background.MaxWidth = lineWidth;
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
                letter.MaskFontSize = size;
                letter.MaskSoft = soft;
            }

            AlignLetters(word, word.Data.Text, size, soft);
        }
        else if (word.Text is { } text)
        {
            Shape(text, size, soft);
        }

        word.FontSize = size;
        word.Element.Margin = new Thickness(0, 0, word.Data.SpaceAfter ? WordGap(size, soft) : 0, 0);
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
        if (on && !row.Rich && row.Line.Words.Count > 0)
        {
            // A hovered line needs its per-word fills; build it even if it is outside
            // the current reading window.
            BuildRichRow(row);
        }

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

    /// <summary>The line's left and right insets: the glow pads by default, or the hover
    /// box's reach in a compact host.</summary>
    private (double Side, double End) TextInsets()
    {
        if (TightInsets)
        {
            // The hover box's outer edge lands on the host's gutter; the ScrollViewer
            // margin is the scrollbar inset. Both sides use the same gutter, so a duet's
            // left- and right-aligned lines sit symmetrically.
            var pad = _fontSize * HoverBoxPad;
            return (
                Math.Max(pad, Gutter - _scroll.Margin.Left + pad),
                Math.Max(pad, Gutter - _scroll.Margin.Right + pad));
        }

        var side = Math.Max(18, _fontSize * 0.5);
        return (side, Math.Max(24, side));
    }

    /// <summary>Gives every word panel the width to wrap against, and the line-timed text
    /// the width to wrap inside. Called from the width the scroll viewer actually has.</summary>
    private void ApplyTextWidth()
    {
        if (_panelWidth <= 0)
        {
            return;
        }

        var (sidePad, endPad) = TextInsets();
        _lines.Margin = new Thickness(sidePad, 0, endPad, 0);
        var contentWidth = Math.Max(0, _panelWidth - sidePad - endPad);
        foreach (var row in _rows)
        {
            // The wrap width just changed: every placement moves, so the row's word
            // gaps are compensated again on the next pass.
            row.SpacingDirty = true;
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

            if (row.Placeholder is { } placeholder)
            {
                placeholder.MaxWidth = contentWidth;
                placeholder.ClearValue(FrameworkElement.HeightProperty);
                row.CachedHeight = 0;
            }

            if (row.BackgroundPlaceholder is { } background)
            {
                background.MaxWidth = contentWidth;
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

    /// <summary>Puts the panel on screen and starts it following the song. The fade is
    /// skipped when the drawer is already open and the panel is merely swapping in.</summary>
    public void PlayEntrance(bool animate = true)
    {
        // A closed panel may have kept the other theme's palette: rebuild the words
        // before they are shown rather than lighting them in the wrong colour.
        if (_pendingLyrics is not null && _paletteTheme != _scroll.ActualTheme)
        {
            Load(_pendingLyrics);
        }

        _visible = true;
        UserScrolled = false;
        if (!_rowsBuilt)
        {
            BuildRows();
        }

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
        if (!animate)
        {
            _lines.Opacity = 1;
            return;
        }

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

    /// <summary>The panel is closed: it stops reading the song and releases the text
    /// surfaces it was showing. The lyrics themselves stay for when it opens again.</summary>
    public void Pause()
    {
        _visible = false;
        ReleaseRowsForClosedPanel();
        UpdateFrame();
    }

    /// <summary>Stops following and painting without releasing the rows, for a panel that
    /// is only swapped out behind the other side panel. Coming back does not rebuild it.</summary>
    public void Suspend()
    {
        _visible = false;
        _tween.Stop();
        _needsGlide = false;
        UpdateFrame();
    }

    private void ReleaseRowsForClosedPanel()
    {
        if (!_rowsBuilt && _rows.Count == 0)
        {
            return;
        }

        _tween.Stop();
        _needsGlide = false;
        ReleaseGlows();
        ReturnAllRowWords();
        CollectDisposedMasks(force: true);
        // The words just went back to the pool. A closed panel has no use for a whole
        // song's worth of native text surfaces: keep a small working set and let the
        // rest be collected, or every visit leaves the previous song parked in memory.
        TrimPools(64, 16);
        _lines.Children.Clear();
        _rows.Clear();
        _rowsBuilt = false;
        _current = null;
        _leadingDots = null;
        _leadingDotItems = [];
        _repaintFrom = -1;
        _repaintTo = -1;
        _needsFullPaint = true;
    }

    /// <summary>Keeps the word pool from holding the biggest song ever heard for the
    /// panel's whole life. The pool's purpose is reuse across songs while the panel is
    /// open; a closed panel needs no more than a small working set, and the surplus's
    /// native text surfaces can be collected instead of parked in the pool.</summary>
    private void TrimPools(int keepPlain, int keepSplit)
    {
        if (_plainWordPool.Count > keepPlain)
        {
            _plainWordPool.RemoveRange(keepPlain, _plainWordPool.Count - keepPlain);
        }

        if (_splitWordPool.Count > keepSplit)
        {
            _splitWordPool.RemoveRange(keepSplit, _splitWordPool.Count - keepSplit);
        }
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

        // The hold is over: the window is scheduled again for the line the seek landed
        // on. Until now every rebuild waited, however many positions the drag crossed.
        if (_glowHoldUntil != 0 && !GlowHeld)
        {
            _glowHoldUntil = 0;
            CollectDisposedMasks();
        }

        var index = Lyrics.IndexOf(_position, _lyricLines) ?? -1;
        // Keep the word-level text around the line being sung; far lines stay as one
        // text block and do not allocate their per-word surfaces.
        EnsureRichWindow(index < 0 ? 0 : index);
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

            // Warm the first window while the intro plays, so the first line's glow is
            // ready when its words start.
            ScheduleGlows(0);
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
        var index = knownIndex ?? (Lyrics.IndexOf(time, _lyricLines) ?? -1);
        // The glow hold builds only for the line being sung; remember which one this
        // frame is painting before the repaint range can touch any letter.
        _activeRowIndex = index;
        EnsureRichWindow(index < 0 ? 0 : index);
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

        PaintInterlude(time, index >= 0 ? index : null);

        var motion = _motion;
        var expressive = _renderer == LyricsRenderer.Expressive;
        var singing = index >= 0 && index < _rows.Count && time <= LineEnd(_rows[index]) + 0.05;
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var sung = IsSung(row, time);
            if (row.PlaceholderBrush is not null || row.BackgroundPlaceholderBrush is not null)
            {
                var color = sung
                    ? expressive
                        ? _expressiveSung
                        : _sung
                    : expressive
                        ? _expressiveUnsung
                        : _unsung;
                if (row.PlaceholderBrush is { } placeholderBrush && placeholderBrush.Color != color)
                {
                    placeholderBrush.Color = color;
                }

                if (row.BackgroundPlaceholderBrush is { } backgroundBrush && backgroundBrush.Color != color)
                {
                    backgroundBrush.Color = color;
                }
            }

            var isActive = singing && i == index;
            var distance = index >= 0 ? Math.Abs(i - index) : int.MaxValue;
            if (expressive && _motion)
            {
                // Every line is a single static text block until it comes close enough to
                // materialize its words. Keep both the far-row placeholder and a line-timed
                // row at the same rest size, so no line shrinks when its detail arrives.
                var rest = time >= row.Line.Time ? 1 : LyricsMotion.WordRestScale;
                if (!row.Rich)
                {
                    SetPlaceholderRest(row, rest);
                }
            }

            row.Opacity = expressive
                ? isActive
                    ? 1
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
                // Expressive lines take their idle-to-full step quickly when they become
                // current; the slow change is the fill front, not a line-wide tween.
                row.Words.Opacity = _playing
                    ? Approach(row.Words.Opacity, wordsTarget, expressive ? 0.1 : 0.18)
                    : wordsTarget;
            }

            if (row.BackgroundItems.Count > 0)
            {
                // Backing vocals step back with their line, at 70% of its weight.
                var target = wordsTarget * 0.7;
                if (Math.Abs(row.Background.Opacity - target) >= 0.001)
                {
                    row.Background.Opacity = _playing
                        ? Approach(row.Background.Opacity, target, expressive ? 0.1 : 0.18)
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

        if (expressive && _motion && _compensateSpacing)
        {
            foreach (var row in _rows)
            {
                // Only a row whose word scales (or layout) changed since its last pass
                // recomputes its gaps. A resting row's shift is a constant; recomputing
                // every row every frame cost more than the paint itself.
                if (!row.Rich || !row.SpacingDirty)
                {
                    continue;
                }

                row.SpacingDirty = false;
                if (row.Words is WordWrapPanel words)
                {
                    CompensateSpacing(row.WordItems, words, row);
                }

                CompensateSpacing(row.BackgroundItems, row.Background, row);
            }
        }
    }

    /// <summary>The end of a line: the provider's end, or its last word's end.</summary>
    private static double LineEnd(Row row) =>
        row.Line.End ?? (row.WordItems.Count > 0 ? row.WordItems[^1].Data.End : row.Line.Time);

    /// <summary>Whether a line is still inside the Expressive settle window.</summary>
    private static bool IsWithinTail(Row row, double time) =>
        time >= row.Line.Time && time <= LineEnd(row) + ExpressiveTailSeconds;

    /// <summary>Keeps the visual gap after each word proportional to its scale. A word's
    /// 0.95 rest scale is a composition transform, so the layout margin would otherwise
    /// stay full size and the spaces would open up. Translate each word to close the
    /// difference; the layout itself is untouched. The placements are read into the row's
    /// own buffer, so the pass allocates nothing per frame.</summary>
    private static void CompensateSpacing(IReadOnlyList<Word> words, WordWrapPanel panel, Row row)
    {
        if (words.Count == 0)
        {
            return;
        }

        var placed = row.Placed ??= new List<PlacedWord>(words.Count);
        placed.Clear();
        foreach (var word in words)
        {
            if (!panel.TryGetPlacement(word.Element, out var placement))
            {
                return;
            }

            var margin = word.Element.Margin;
            var width = placement.Width - margin.Left - margin.Right;
            if (width < 1)
            {
                width = Math.Max(1, word.Element.DesiredSize.Width - margin.Left - margin.Right);
            }

            placed.Add(new PlacedWord(word, placement.X + margin.Left, placement.Y, width, margin.Left, margin.Right));
        }

        var rightAligned = panel.AlignRight;
        var start = 0;
        while (start < placed.Count)
        {
            var end = start + 1;
            while (end < placed.Count && Math.Abs(placed[end].Y - placed[start].Y) < 0.5)
            {
                end++;
            }

            if (rightAligned)
            {
                var shift = 0.0;
                for (var i = end - 1; i >= start; i--)
                {
                    var part = placed[i];
                    var scale = ScaleOf(part.Word);
                    var origin = part.Word.OriginX * part.Width;
                    if (i == end - 1)
                    {
                        shift = (part.Width - origin) * (1 - scale);
                    }

                    SetSpacingShift(part.Word, shift);
                    if (i > start)
                    {
                        var next = placed[i];
                        var nextScale = ScaleOf(next.Word);
                        var nextOrigin = next.Word.OriginX * next.Width;
                        var nextLeft = next.X + shift + nextOrigin * (1 - nextScale);
                        var prev = placed[i - 1];
                        var prevScale = ScaleOf(prev.Word);
                        var prevOrigin = prev.Word.OriginX * prev.Width;
                        var desired = prev.MarginRight * prevScale;
                        shift = nextLeft - desired -
                            (prev.X + prev.Width * prevScale + prevOrigin * (1 - prevScale));
                    }
                }
            }
            else
            {
                var shift = 0.0;
                for (var i = start; i < end; i++)
                {
                    var part = placed[i];
                    var scale = ScaleOf(part.Word);
                    var origin = part.Word.OriginX * part.Width;
                    if (i == start)
                    {
                        shift = -origin * (1 - scale);
                    }

                    SetSpacingShift(part.Word, shift);
                    if (i < end - 1)
                    {
                        var next = placed[i + 1];
                        var nextScale = ScaleOf(next.Word);
                        var nextOrigin = next.Word.OriginX * next.Width;
                        var right = part.X + shift + part.Width * scale + origin * (1 - scale);
                        var desired = part.MarginRight * scale;
                        shift = right + desired - (next.X + nextOrigin * (1 - nextScale));
                    }
                }
            }

            start = end;
        }
    }

    private static double ScaleOf(Word word) =>
        word.PaintedScale > 0 ? word.PaintedScale : LyricsMotion.WordRestScale;

    private static void SetSpacingShift(Word word, double shift)
    {
        word.SpaceX = shift;
        SetWordTranslation(word, word.MotionX + shift, word.MotionY);
    }

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
            if (index >= 0 && index < _rows.Count &&
                index + 1 < _lyricLines.Count &&
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
                stops[0].Color = _unsung;
                stops[1].Color = _unsung;
                stops[2].Color = _unsung;
                stops[1].Offset = 0;
                stops[2].Offset = 1;
            }
            else if (fill >= 1)
            {
                stops[0].Color = _sung;
                stops[1].Color = _sung;
                stops[2].Color = _sung;
                stops[1].Offset = 1;
                stops[2].Offset = 1;
            }
            else
            {
                stops[0].Color = _sung;
                stops[1].Color = _sung;
                stops[2].Color = _unsung;
                stops[1].Offset = Math.Max(0, front - feather);
                stops[2].Offset = Math.Min(1, Math.Max(front, 0.0001));
            }
        }

        // The word rises as it is sung: a ripple through the line.
        if (Math.Abs(eased - word.PaintedRise) >= 0.001)
        {
            word.PaintedRise = eased;
            SetWordTranslation(word, 0, -_risePixels * eased);
        }

        // A note held long swells, by as much as the look asks for.
        var glow = _glow ? Math.Clamp(_glowIntensity, 0, 2) : 0;
        var swell = 1 + SwellScale * sustain * glow * (classic ? 0 : 1);
        if (Math.Abs(swell - word.PaintedSwell) >= 0.0005)
        {
            word.PaintedSwell = swell;
            SetWordScale(word, swell);
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
        var inWordRange = elapsed >= 0 && elapsed <= duration;
        var progress = Math.Clamp(elapsed / duration, 0, 1);
        var lit = -0.2 + 1.2 * progress;
        var soft = lit + _feather;

        var sung = hovered
            ? active
                ? _expressiveActiveHoverSung
                : _expressiveHoverSung
            : _expressiveSung;
        var unsung = hovered
            ? active
                ? _expressiveActiveHoverUnsung
                : _expressiveHoverUnsung
            : _expressiveUnsung;

        var wordMotion = motion
            ? LyricsMotion.Word(elapsed, duration, word.PushX)
            : new WordMotion(1, 0, 0);
        SetWordMotion(word, wordMotion);

        if (word.LetterCount > 0)
        {
            _paintedLetters += word.LetterCount;
            var strength = _glow ? Math.Clamp(_glowIntensity, 0, 2) : 0;
            for (var letterIndex = 0; letterIndex < word.LetterCount; letterIndex++)
            {
                var letter = word.Letters[letterIndex];
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

                SetLetterMotion(letter, letterMotion, word.FontSize, strength, data.End, inWordRange);
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

    /// <summary>Writes a scale to a composition visual, around the same point the XAML
    /// render transform used. Layout keeps owning the visual's offset, so scaling here
    /// never forces the text to be measured or rasterized again.</summary>
    private static void ApplyScale(
        Visual? visual,
        FrameworkElement? element,
        double scale,
        double originX,
        double originY)
    {
        if (visual is null)
        {
            return;
        }

        ApplyCenter(visual, element, originX, originY);
        visual.Scale = new Vector3((float)scale, (float)scale, 1);
    }

    /// <summary>Keeps the scale origin on the element's current size. A pooled word is
    /// measured again after it is reattached, and a scale written before that layout
    /// would otherwise make the word jump the first time it is painted.</summary>
    private static void ApplyCenter(
        Visual? visual,
        FrameworkElement? element,
        double originX,
        double originY)
    {
        if (visual is null || element is null)
        {
            return;
        }

        var center = new Vector3(
            (float)(element.ActualWidth * originX),
            (float)(element.ActualHeight * originY),
            0);
        if (visual.CenterPoint != center)
        {
            visual.CenterPoint = center;
        }
    }

    private static void TrackCenter(Word word)
    {
        word.Element.SizeChanged += (_, e) =>
        {
            if (word.Visual is { } visual)
            {
                visual.CenterPoint = new Vector3(
                    (float)(e.NewSize.Width * word.OriginX),
                    (float)(e.NewSize.Height * word.OriginY),
                    0);
            }
        };
    }

    private static void TrackCenter(Letter letter, FrameworkElement element, Visual? visual)
    {
        element.SizeChanged += (_, e) =>
        {
            if (visual is not null)
            {
                visual.CenterPoint = new Vector3(
                    (float)(e.NewSize.Width / 2),
                    (float)(e.NewSize.Height / 2),
                    0);
            }
        };
    }

    private static void ApplyTranslation(Visual? visual, double x, double y)
    {
        visual?.Properties.InsertVector3("Translation", new Vector3((float)x, (float)y, 0));
    }

    private static void SetWordScale(Word word, double scale)
    {
        word.PaintedScale = scale;
        // The spacing compensation is a function of each word's scale: only a row whose
        // scale actually moved needs its word gaps recomputed this frame.
        word.Owner?.SpacingDirty = true;
        ApplyScale(word.Visual, word.Element, scale, word.OriginX, word.OriginY);
    }

    private static void SetWordTranslation(Word word, double x, double y)
    {
        if (double.IsNaN(word.PaintedX)
            || Math.Abs(x - word.PaintedX) >= 0.01
            || Math.Abs(y - word.PaintedY) >= 0.01)
        {
            word.PaintedX = x;
            word.PaintedY = y;
            ApplyTranslation(word.Visual, x, y);
        }
    }

    private static void SetWordMotion(Word word, WordMotion motion)
    {
        if (Math.Abs(motion.Scale - word.PaintedScale) >= 0.0005)
        {
            SetWordScale(word, motion.Scale);
        }

        word.MotionX = motion.X * word.FontSize;
        word.MotionY = motion.Y * word.FontSize;
        SetWordTranslation(word, word.MotionX + word.SpaceX, word.MotionY);
    }

    private void SetLetterMotion(
        Letter letter,
        LetterMotion motion,
        double fontSize,
        double glowStrength,
        double wordEnd,
        bool wordInRange)
    {
        if (Math.Abs(motion.Scale - letter.PaintedScale) >= 0.0005)
        {
            letter.PaintedScale = motion.Scale;
            ApplyScale(letter.Visual, letter.Text, motion.Scale, 0.5, 0.5);
            if (!_glowTransformFixed)
            {
                ApplyScale(letter.GlowHostVisual, letter.GlowHost, motion.Scale, 0.5, 0.5);
            }
        }

        var x = motion.X * fontSize;
        var y = motion.Y * fontSize;
        if (double.IsNaN(letter.PaintedX)
            || Math.Abs(x - letter.PaintedX) >= 0.01
            || Math.Abs(y - letter.PaintedY) >= 0.01)
        {
            letter.PaintedX = x;
            letter.PaintedY = y;
            ApplyTranslation(letter.Visual, x, y);
            if (!_glowTransformFixed)
            {
                ApplyTranslation(letter.GlowHostVisual, x, y);
            }
        }

        if (letter.NeedsMaskRefresh && letter.Shadow is not null)
        {
            RefreshGlowMask(letter);
        }

        var glow = motion.Glow * glowStrength;
        if (glow > 0.01)
        {
            letter.GlowIdleSince = -1;
            EnsureGlow(letter, painting: wordInRange);
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
            if (letter.GlowIdleSince < 0)
            {
                letter.GlowIdleSince = _frameClock.ElapsedMilliseconds;
            }

            var idleBlur = _glowBlurFixed ? 12f : 0f;
            if (letter.Shadow is { } idleShadow
                && (letter.PaintedGlow != 0 || letter.PaintedBlur != idleBlur))
            {
                idleShadow.Opacity = 0;
                idleShadow.BlurRadius = idleBlur;
                letter.PaintedGlow = 0;
                letter.PaintedBlur = idleBlur;
            }

            // The halo is invisible now (opacity 0). Free the sprite and its mask here
            // instead of holding them until the line leaves the ±1 window, but wait out a
            // brief dip first; past the word's settle tail the glow cannot return, so the
            // surfaces go at once.
            if (letter.GlowShown && letter.PaintedGlow == 0)
            {
                var idle = _frameClock.ElapsedMilliseconds - letter.GlowIdleSince;
                if (idle >= 300 || _position > wordEnd + LyricsMotion.SettleSeconds)
                {
                    ReleaseGlow(letter, keepMask: true);
                    letter.GlowDone = true;
                }
            }
        }
    }

    /// <summary>Whether a seek's brief hold is still suppressing glow rebuilds. A drag or
    /// a skip can raise a window of masks for every position it crosses; waiting for the
    /// position to settle makes it raise one window instead.</summary>
    private bool GlowHeld => _glowHoldUntil > _frameClock.ElapsedMilliseconds;

    /// <summary>The element a letter's alpha mask is taken from: the hidden twin when the
    /// static-mask mode is on, or the sharp letter itself.</summary>
    private TextBlock MaskSource(Letter letter) =>
        _glowStaticMask && letter.MaskText is { } twin ? twin : letter.Text;

    /// <summary>Whether the mask's source has been laid out. A twin added this frame has
    /// no size until the next layout pass, and GetAlphaMask needs one.</summary>
    private bool MaskSourceHasSize(Letter letter) =>
        _glowStaticMask && letter.MaskText is { } twin
            ? twin.ActualWidth >= 0.5 && twin.ActualHeight >= 0.5
            : letter.Text.ActualWidth >= 0.5 && letter.Text.ActualHeight >= 0.5;

    /// <summary>Disposes a letter's alpha mask. Only call this when the twin it came from
    /// is replaced at the same time.</summary>
    private void DisposeMask(Letter letter)
    {
        if (letter.Shadow is { } shadow)
        {
            shadow.Mask = null;
        }

        var mask = letter.Mask;
        letter.Mask = null;
        if (mask is null)
        {
            return;
        }

        mask.Dispose();
        _masksDisposed++;
        _masksDisposedSinceCollection++;
    }

    /// <summary>Takes a letter's mask twin out of the tree after its mask was disposed.
    /// The twin is never reused: GetAlphaMask may hand the same, now-disposed brush back
    /// for the same element. The fresh one is built by <see cref="EnsureMaskTwin"/> only
    /// when the glow is wanted again - not on every release, which allocated a new native
    /// text surface per letter each time a line left the glow window.</summary>
    private void DropMaskTwin(Letter letter)
    {
        if (letter.MaskWrapper is { } old && letter.GlowHost is { } host)
        {
            host.Children.Remove(old);
        }

        letter.MaskText = null;
        letter.MaskWrapper = null;
        letter.NeedsMaskRefresh = true;
    }

    /// <summary>Gives a letter a fresh mask twin when its glow is wanted again. The twin
    /// needs a layout pass before its alpha can be taken, so it is built one frame ahead
    /// of the mask - exactly as a letter's first twin was at build time.</summary>
    private void EnsureMaskTwin(Letter letter)
    {
        if (!_glowStaticMask || letter.MaskText is not null || letter.GlowHost is not { } host)
        {
            return;
        }

        var (text, wrapper) = MakeMaskTwin(letter.Text.Text, letter.MaskFontSize, letter.MaskSoft);
        host.Children.Add(wrapper);
        letter.MaskText = text;
        letter.MaskWrapper = wrapper;
        letter.NeedsMaskRefresh = true;
    }

    /// <summary>Lazily gives a letter its Gaussian glow. The sprite lives in an empty
    /// layer before the text, so the shadow is drawn behind the sharp glyph.
    /// <paramref name="painting"/> says the letter is being painted with a live glow in its
    /// own word range, which may rebuild a glow that ended earlier.</summary>
    private void EnsureGlow(Letter letter, bool painting = false)
    {
        if (!_glow || letter.GlowHost is null || letter.GlowVisual is not null)
        {
            return;
        }

        // While a seek settles, only the line being sung may build its glow.
        if (GlowHeld && letter.RowIndex != _activeRowIndex)
        {
            return;
        }

        // A glow that already ended stays ended until it is painted again inside its own
        // word; a queued warmup or a stray repaint must not rebuild it.
        if (letter.GlowDone && !painting)
        {
            return;
        }

        var width = letter.Text.ActualWidth;
        var height = letter.Text.ActualHeight;
        if (width < 0.5 || height < 0.5)
        {
            return; // the first paint can run before the letter has a size
        }

        if (_glowStaticMask && !_glowNoMask && letter.MaskText is null)
        {
            // The twin went with the mask when the glow was released: a fresh one must
            // exist before any mask is taken, and it needs a layout pass before its
            // alpha can be read, so its mask arrives a frame later. (Only the static-twin
            // mode has twins; the other modes take the mask from the sharp letter.)
            EnsureMaskTwin(letter);
            return;
        }

        if (!_glowNoMask && !MaskSourceHasSize(letter))
        {
            return; // a fresh twin is measured on the next layout pass
        }

        if (!_glowNoMask && letter.Mask is not null && letter.NeedsMaskRefresh
            && letter.MaskWrapper is not null)
        {
            // The font or size changed. Dispose the stale mask with its twin, then let
            // the layout measure the fresh twin before its alpha is taken.
            DisposeMask(letter);
            DropMaskTwin(letter);
            EnsureMaskTwin(letter);
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(letter.GlowHost);
        var compositor = visual.Compositor;
        var shadow = compositor.CreateDropShadow();
        shadow.Color = _sung;
        shadow.Offset = Vector3.Zero;
        shadow.Opacity = 0;
        shadow.BlurRadius = _glowBlurFixed ? 12 : 0;
        if (!_glowNoMask)
        {
            if (letter.Mask is null || letter.NeedsMaskRefresh)
            {
                // Keep the mask while this letter stays in the live window; a seek
                // back into the same window can reuse it instead of taking a new one.
                letter.Mask = MaskSource(letter).GetAlphaMask();
                _masksCreated++;
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
        _glowsBuilt++;
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

        if (letter.NeedsMaskRefresh && letter.Mask is not null && letter.MaskWrapper is not null)
        {
            // The font or size changed: the old mask is stale, and it may only be
            // disposed together with the twin that produced it.
            DisposeMask(letter);
            DropMaskTwin(letter);
            EnsureMaskTwin(letter);
        }

        EnsureMaskTwin(letter);
        if (!MaskSourceHasSize(letter))
        {
            return; // the fresh twin is measured on the next layout pass
        }

        var mask = MaskSource(letter).GetAlphaMask();
        _masksCreated++;
        if (!ReferenceEquals(letter.Mask, mask))
        {
            letter.Shadow.Mask = mask;
            letter.Mask = mask;
        }

        letter.NeedsMaskRefresh = false;
    }

    private void ReleaseGlow(Letter letter, bool keepMask = false)
    {
        // Detach the shadow from its mask before the shadow goes, then take the sprite
        // off its host. Dispose frees the composition objects; the native surfaces behind
        // the mask are only released once the brush itself is collected.
        if (letter.Shadow is { } shadow)
        {
            shadow.Mask = null;
        }

        if (letter.GlowHost is { } host)
        {
            ElementCompositionPreview.SetElementChildVisual(host, null);
        }

        letter.Shadow?.Dispose();
        letter.GlowVisual?.Dispose();
        letter.GlowBrush?.Dispose();
        letter.Shadow = null;
        letter.GlowVisual = null;
        letter.GlowBrush = null;

        if (!keepMask && letter.Mask is not null)
        {
            if (letter.MaskWrapper is not null)
            {
                // The letter is leaving the live window: dispose the mask together with
                // the twin that produced it, and drop the twin rather than replacing it.
                // GetAlphaMask may hand the same brush back for the same element, so the
                // next mask must come from a fresh element - built lazily when the glow
                // is wanted again, not eagerly on every release.
                DisposeMask(letter);
                DropMaskTwin(letter);
            }
            else
            {
                // No twin to replace (a test mode takes the mask from the sharp letter):
                // only drop the reference, since the same element may hand it back.
                letter.Mask = null;
            }
        }

        letter.GlowShown = false;
        letter.PaintedGlow = -1;
        letter.PaintedBlur = -1;
        letter.GlowIdleSince = -1;

        // The mask's native surface is only released once the brush is collected. A long
        // song can dispose hundreds of them, so collect when the backlog grows instead of
        // waiting for a collection the small managed heap may not provoke for minutes.
        CollectDisposedMasks();
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

    /// <summary>Runs one full collection on a background thread. The managed heap is
    /// small; the point is to run finalizers for the native text and composition surfaces
    /// that were just released. Never more than one is in flight.</summary>
    private void ForceCollection()
    {
        _collectionsForced++;
        _masksDisposedSinceCollection = 0;
        if (System.Threading.Interlocked.Exchange(ref _collectionRunning, 1) != 0)
        {
            return;
        }

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _collectionRunning, 0);
            }
        });
    }

    /// <summary>Native text/composition surfaces are released by dropping their references,
    /// not by Dispose, so they wait for a collection a small managed heap rarely provokes.
    /// Collect at most once a minute while playing, and only when a real backlog of masks
    /// has piled up; the song change's own collection and the natural collections take the
    /// rest. A mid-playback collection stalls the follow glide for a frame or two, so a
    /// skip burst must not be able to fire one per few seconds.</summary>
    private void CollectDisposedMasks(bool force = false)
    {
        if (!force && _masksDisposedSinceCollection < MaskCollectionThreshold)
        {
            return;
        }

        if (_masksDisposedSinceCollection == 0)
        {
            return;
        }

        if (!force)
        {
            var now = _frameClock.ElapsedMilliseconds;
            if (now - _lastMaskCollectionMs < MaskCollectionIntervalMs)
            {
                return;
            }

            _lastMaskCollectionMs = now;
        }

        ForceCollection();
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
        // The seek hold owns the window: it is scheduled once when the hold ends.
        if (!_glow || GlowHeld || index == _glowWindowIndex)
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
                for (var letterIndex = 0; letterIndex < word.LetterCount; letterIndex++)
                {
                    var letter = word.Letters[letterIndex];
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
                for (var letterIndex = 0; letterIndex < word.LetterCount; letterIndex++)
                {
                    var letter = word.Letters[letterIndex];
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
        if (GlowHeld)
        {
            return;
        }

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

            // A letter with no twin yet goes straight to EnsureGlow, which builds the
            // twin and waits a layout pass; the mask follows on a later warmup tick or
            // the first painted frame, exactly as a fresh letter's first glow did.
            if (letter.MaskText is not null && !MaskSourceHasSize(letter))
            {
                continue;
            }

            EnsureGlow(letter);
            if (letter.GlowVisual is null && letter.GlowWanted && !letter.GlowDone)
            {
                // The pass only built the letter's fresh mask twin; its alpha needs a
                // layout pass first. Come back next frame for the mask itself, or a
                // glow that was freed and re-armed pops in at the frame it starts.
                if (_glowWarmupSet.Add(letter))
                {
                    _glowWarmup.Enqueue(letter);
                }
            }

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
                $"masks={_masksCreated} disposed={_masksDisposed} built={_glowsBuilt} " +
                $"forced={_collectionsForced} hold={(GlowHeld ? 1 : 0)} " +
                $"pools={_plainWordPool.Count}+{_splitWordPool.Count} " +
                $"made={_plainWordsMade}+{_splitWordsMade}/{_lettersMade} " +
                $"private={privateBytes / 1048576.0:F1}MB growth={growth:F1}MB/s{Environment.NewLine}");
        }
        catch (Exception)
        {
            // diagnostics must not take the panel down
        }
    }

    /// <summary>Diagnostic only: records a backward step under a second, so a position
    /// source that jitters can be told from a real seek. The jump thresholds are left
    /// exactly as they are.</summary>
    private void LogBackwardStep(double back)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "koda-lyrics-live.txt"),
                $"{DateTime.Now:HH:mm:ss.fff} back={back:F3}s from={_position:F3} " +
                $"to={_position - back:F3}{Environment.NewLine}");
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
        _tweenClock.Restart();
        _tween.Start();
    }

    private double _tweenStart;
    private double _tweenTarget;

    /// <summary>How long the glide has actually been running. The timer's ticks are not
    /// exactly 16 ms under load; counting assumed ticks made a half-second glide last
    /// several seconds, and that was the panel holding the listener's scroll.</summary>
    private readonly System.Diagnostics.Stopwatch _tweenClock = new();

    private void OnTweenTick(object? sender, object args)
    {
        var k = Math.Min(1.0, _tweenClock.Elapsed.TotalMilliseconds / GlideMilliseconds);
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

    /// <summary>The sung colour at another alpha, for the unlit text that has no word masks:
    /// plain lyrics, line-timed lines, credits and the interlude dots.</summary>
    private SolidColorBrush Brush(byte alpha) =>
        new(Windows.UI.Color.FromArgb(alpha, _sung.R, _sung.G, _sung.B));

    /// <summary>The layout gap used after a word. With compensation on, it is the font's
    /// own space and the per-word shift keeps it proportional. With compensation off, the
    /// gap is reduced once in layout so it matches the word's 0.95 rest; no word moves later.</summary>
    private double WordGap(double fontSize, bool soft) =>
        _allRowsRich
            ? WordSpace(fontSize, soft) + fontSize * 0.06
            : WordSpace(fontSize, soft) *
                (_compensateSpacing ? 1 : LyricsMotion.WordRestScale);

    /// <summary>The width the font itself gives a space, so word-level margins match the
    /// one-block placeholder that uses real spaces. A fixed em guess made the spaces change
    /// when a line was promoted to word-level text.</summary>
    private double WordSpace(double fontSize, bool soft)
    {
        if (soft)
        {
            if (Math.Abs(fontSize - _spaceSoftSize) < 0.01 && _spaceSoftSize >= 0)
            {
                return _spaceSoftWidth;
            }
        }
        else if (Math.Abs(fontSize - _spaceSize) < 0.01 && _spaceSize >= 0)
        {
            return _spaceWidth;
        }

        var spaced = new TextBlock { Text = "| |", TextWrapping = TextWrapping.NoWrap };
        var bare = new TextBlock { Text = "||", TextWrapping = TextWrapping.NoWrap };
        Shape(spaced, fontSize, soft);
        Shape(bare, fontSize, soft);
        spaced.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        bare.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Max(0, spaced.DesiredSize.Width - bare.DesiredSize.Width);
        if (soft)
        {
            _spaceSoftSize = fontSize;
            _spaceSoftWidth = width;
        }
        else
        {
            _spaceSize = fontSize;
            _spaceWidth = width;
        }

        return width;
    }

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
        _scroll.ActualThemeChanged -= OnActualThemeChanged;
        ReleaseGlows();
        _hoverOpacityIn?.Dispose();
        _hoverOpacityOut?.Dispose();
        _hoverScaleIn?.Dispose();
        _hoverScaleOut?.Dispose();
        _hoverEase?.Dispose();
        _tween.Stop();
        _frame.Stop();
    }
}


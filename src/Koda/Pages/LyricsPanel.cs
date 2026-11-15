using System;
using System.Collections.Generic;
using System.Linq;
using Koda.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    private double _baseLineGap = 24;
    private double _fontSize = 36;
    private double _lineGap = 24;
    private BackingVocalsMode _backingMode = BackingVocalsMode.Shown;
    private double _idleNear = 0.5;
    private double _idleFar = 0.36;
    private bool _glow = true;
    private double _glowIntensity = 1;
    private bool _motion = true;
    private LyricsRenderer _renderer = LyricsRenderer.Sweep;

    private readonly ScrollViewer _scroll;
    private readonly StackPanel _lines;
    private readonly Border _topSpacer = new();
    private readonly Border _spacer = new();
    private readonly DispatcherQueueTimer _tween;
    private readonly DispatcherQueueTimer _frame;

    private readonly List<Row> _rows = [];
    private readonly List<LyricLine> _lyricLines = [];
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
    private double _position;
    private double _panelWidth;
    private double _panelHeight;

    /// <summary>One word in the panel: the element that draws it and the timings behind it.</summary>
    private sealed class Word
    {
        public required LyricWord Data { get; init; }
        public required TextBlock Element { get; init; }
        /// <summary>The mask the word fills through.</summary>
        public required LinearGradientBrush Fill { get; init; }
        /// <summary>The rise and the swell of the word.</summary>
        public required CompositeTransform Transform { get; init; }
        /// <summary>Whether this is a syllable of the word before it: the two may not be
        /// split by a line break.</summary>
        public bool JoinsPrevious { get; init; }
        /// <summary>The fill last written, so a word that is not changing is not redrawn.</summary>
        public double PaintedFill { get; set; } = -1;
        /// <summary>The rise last written.</summary>
        public double PaintedRise { get; set; } = -1;
        /// <summary>The swell last written.</summary>
        public double PaintedSwell { get; set; } = -1;
    }

    /// <summary>One line in the panel: its words, its backing vocals and the line itself.</summary>
    private sealed class Row
    {
        public required LyricLine Line { get; init; }
        /// <summary>The element that holds the line, for finding its place in the panel.</summary>
        public FrameworkElement Host { get; set; } = null!;
        public required StackPanel Words { get; init; }
        public required StackPanel Background { get; init; }
        public List<Word> WordItems { get; } = [];
        public List<Word> BackgroundItems { get; } = [];
        /// <summary>The three dots of the gap that follows this line, when the gap is long.</summary>
        public List<Ellipse> Dots { get; } = [];
        /// <summary>The dots' container; shown only while that gap is playing.</summary>
        public StackPanel? DotsHost { get; set; }
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
            if (PositionSource is { } source)
            {
                SetPosition(source());
            }

            // Following the song here, not on the app's twice-a-second tick: the line
            // being sung changes the moment the song reaches it.
            Follow();
        };

        // The words are laid out in rows that fit the panel, so a line breaks where it should.
        _scroll.SizeChanged += (_, _) => RefreshLayout();
    }

    /// <summary>Re-reads the panel's size and reflows. Called when the window moved to
    /// another display or zoom, where the ScrollViewer's own SizeChanged can be missed.</summary>
    public void RefreshLayout()
    {
        var width = _scroll.ActualWidth;
        var height = _scroll.ActualHeight;
        if (width < 1)
        {
            return; // collapsed: keep the size the panel had when it was on screen
        }

        var widthChanged = Math.Abs(width - _panelWidth) >= 1;
        var heightChanged = Math.Abs(height - _panelHeight) >= 1;
        if (!widthChanged && !heightChanged)
        {
            return;
        }

        _panelWidth = width;
        _panelHeight = height;
        UpdateSpacers();

        var oldSize = _fontSize;
        UpdateScale();
        var resized = Math.Abs(_fontSize - oldSize) >= 0.25;
        if (resized)
        {
            Reshape();
        }

        if (resized || widthChanged)
        {
            Reflow();
        }

        // The panel now knows how tall it is: put the line being sung where it belongs.
        if (_current is not null && !_userScrolled)
        {
            GlideTo(_current);
        }

        // Apply the new size to the words already on screen, even while paused.
        if (resized)
        {
            Paint();
        }
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
        // or the line that was partly sung keeps its half-filled words.
        if (next < _position - 0.15 || next > _position + 0.6)
        {
            _needsFullPaint = true;
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
        _glow = _glowIntensity > 0;
        _motion = settings.LyricsMotion;
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
            Reflow();
            return;
        }

        // A long wait before the first line gets its dots too.
        if (lyrics.Lines.Count > 0 && lyrics.Lines[0].Time >= InterludeSeconds)
        {
            _leadingDotItems = [];
            _leadingDots = MakeDots(_leadingDotItems, _fontSize);
            _lines.Children.Add(_leadingDots);
        }

        for (var index = 0; index < lyrics.Lines.Count; index++)
        {
            var line = lyrics.Lines[index];
            var fontSize = _fontSize;
            var row = new Row
            {
                Line = line,
                Words = new StackPanel { Spacing = 4 },
                Background = new StackPanel { Spacing = 2, Opacity = _idleFar * 0.7 },
            };

            var halfGap = _lineGap / 2;
            var host = new StackPanel { Margin = new Thickness(0, halfGap, 0, halfGap), Spacing = 6 };
            // A duet's second singer sits on the right. Everyone else sits on the left,
            // including a group singing together: centering those lines made a duet's
            // simultaneous lines read as one block in the middle of the panel.
            var side = line.Voice == LyricVoice.Duet
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Left;
            host.HorizontalAlignment = side;
            row.Words.HorizontalAlignment = side;
            row.Background.HorizontalAlignment = side;
            var textSide = side == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Left;

            host.Children.Add(row.Words);

            row.Host = host;

            // A tap on the line jumps the song to it, and hands the panel back to the song.
            host.Tapped += (_, _) =>
            {
                UserScrolled = false;
                Seek?.Invoke(line.Time);
            };

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
                for (var wordIndex = 0; wordIndex < line.Words.Count; wordIndex++)
                {
                    var item = MakeWord(
                        line.Words[wordIndex],
                        fontSize,
                        joinsPrevious: JoinsPrevious(line, wordIndex));
                    row.WordItems.Add(item);
                }
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
            }

            if (row.BackgroundItems.Count > 0)
            {
                host.Children.Add(row.Background);
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
                row.DotsHost = MakeDots(row.Dots, fontSize);
                _lines.Children.Add(row.DotsHost);
            }
        }

        // The room below the last line, so the final lines of a song can still be scrolled
        // up to the reading position.
        _lines.Children.Add(_spacer);

        // A song opened part way through needs its earlier lines painted as complete and
        // its later lines left unsung, not everything starting grey.
        _needsFullPaint = true;

        Reflow();
    }

    /// <summary>Three dots that fill in turn through an instrumental gap; they grow with
    /// the words so the pause reads at any size.</summary>
    private static StackPanel MakeDots(List<Ellipse> dots, double fontSize)
    {
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Visibility = Visibility.Collapsed,
        };
        var diameter = DotsDiameter(fontSize);
        for (var dot = 0; dot < 3; dot++)
        {
            var ellipse = new Ellipse { Width = diameter, Height = diameter, Fill = Brush(0x4D) };
            host.Children.Add(ellipse);
            dots.Add(ellipse);
        }

        SizeDots(host, dots, fontSize);
        return host;
    }

    private static double DotsDiameter(double fontSize) => Math.Max(4, Math.Round(fontSize * 0.18));

    private static void SizeDots(StackPanel host, List<Ellipse> dots, double fontSize)
    {
        var diameter = DotsDiameter(fontSize);
        host.Spacing = diameter;
        host.Margin = new Thickness(0, diameter * 0.6, 0, diameter * 0.6);
        foreach (var dot in dots)
        {
            dot.Width = diameter;
            dot.Height = diameter;
        }
    }

    /// <summary>One word: its element, the mask it fills through, and its rise.</summary>
    private Word MakeWord(LyricWord word, double fontSize, bool soft = false, bool joinsPrevious = false)
    {
        var fill = new LinearGradientBrush
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

        var transform = new CompositeTransform();
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
            Fill = fill,
            Transform = transform,
            JoinsPrevious = joinsPrevious,
        };
    }

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

    /// <summary>
    /// The words of a line are set left to right and wrapped like text, so a line breaks
    /// the same way whether it is being sung or resting.
    /// </summary>
    private void Reflow()
    {
        UpdateSpacers();

        var width = _panelWidth - 24;
        if (width <= 0)
        {
            return;
        }

        foreach (var row in _rows)
        {
            // A line-timed row holds its line text directly; only word rows are laid out.
            if (row.WordItems.Count > 0)
            {
                LayOut(row.Words, row.WordItems, width);
            }

            if (row.BackgroundItems.Count > 0)
            {
                LayOut(row.Background, row.BackgroundItems, width);
            }
        }
    }

    /// <summary>Re-shapes the words already on the panel when the fitted text size changes
    /// (a narrower window, a DPI change or UI zoom), keeping the same elements.</summary>
    private void Reshape()
    {
        foreach (var row in _rows)
        {
            row.Host.Margin = new Thickness(0, _lineGap / 2, 0, _lineGap / 2);
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
                }
            }

            if (row.DotsHost is { } dots)
            {
                SizeDots(dots, row.Dots, _fontSize);
            }
        }

        if (_leadingDots is { } leading)
        {
            SizeDots(leading, _leadingDotItems, _fontSize);
        }
    }

    private void ReshapeWord(Word word, double size, bool soft)
    {
        Shape(word.Element, size, soft);
        word.Element.Margin = new Thickness(0, 0, word.Data.SpaceAfter ? size * WordSpacing : 0, 0);
        word.Element.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        word.PaintedRise = -1; // the lift distance changed with the size
    }

    /// <summary>The room above and below the words: the first and last lines can reach the
    /// reading position, and the fade at the top never swallows the opening lines.</summary>
    private void UpdateSpacers()
    {
        _topSpacer.Height = Math.Max(0, _scroll.ViewportHeight * ReadingAnchor);
        _spacer.Height = Math.Max(0, _scroll.ViewportHeight);
    }

    private void LayOut(StackPanel panel, List<Word> words, double width)
    {
        // Each word still belongs to the line it was laid out on last time; clearing the panel
        // drops those lines but not the words from them, and a XAML element cannot be added to
        // a second parent (0x800F1000, which crashed the app on a resize).
        foreach (var old in panel.Children.OfType<StackPanel>())
        {
            old.Children.Clear();
        }

        panel.Children.Clear();
        StackPanel? line = null;
        var used = 0.0;
        for (var index = 0; index < words.Count;)
        {
            // A word the provider split into syllables is one unit: a line may not break
            // inside it. The cap keeps a language without spaces from making the line one
            // unbreakable run.
            var start = index;
            var unitWidth = 0.0;
            var parts = 0;
            do
            {
                // DesiredSize already carries the word's trailing-space margin; adding the
                // margin again made a line break one word early, leaving a short ragged line.
                unitWidth += words[index].Element.DesiredSize.Width;
                index++;
                parts++;
            }
            while (index < words.Count && words[index].JoinsPrevious && parts < 4);

            if (line is null || used + unitWidth > width)
            {
                line = new StackPanel { Orientation = Orientation.Horizontal };
                panel.Children.Add(line);
                used = 0;
            }

            for (var part = start; part < index; part++)
            {
                line.Children.Add(words[part].Element);
            }

            used += unitWidth;
        }
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
            Reflow();
        }

        // The panel has just come into view: light the line being sung and place it now
        // that the panel knows its height (before that, there was nothing to measure against).
        Follow();
        if (_current is not null)
        {
            GlideTo(_current);
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

        Paint();
    }

    /// <summary>
    /// The words of the line being sung, redrawn every frame: the mask runs across the
    /// word, the word rises as it starts, a note held long swells and glows.
    /// </summary>
    private void Paint()
    {
        if (_needsGlide && _current is not null && _scroll.ViewportHeight > 1)
        {
            _needsGlide = false;
            GlideTo(_current);
        }

        var time = _position;
        if (_needsFullPaint)
        {
            _needsFullPaint = false;
            RepaintWords(time);
        }

        PaintInterlude(time);

        var index = Lyrics.IndexOf(time, _lyricLines) ?? -1;
        var motion = _motion;
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var sung = IsSung(row, time);
            var distance = index >= 0 ? Math.Abs(i - index) : int.MaxValue;
            row.Opacity = sung ? 1 : distance <= 1 ? _idleNear : _idleFar;

            // The rows step back with a fade alone: scaling them by a hair made a new line
            // snap when the fill took over the transform.
            if (Math.Abs(row.Words.Opacity - row.Opacity) >= 0.001)
            {
                row.Words.Opacity = Approach(row.Words.Opacity, row.Opacity);
            }

            if (row.BackgroundItems.Count > 0 &&
                Math.Abs(row.Background.Opacity - row.Opacity * 0.7) >= 0.001)
            {
                // Backing vocals step back with their line, at 70% of its weight.
                row.Background.Opacity = Approach(row.Background.Opacity, row.Opacity * 0.7);
            }

            if (!sung)
            {
                continue;
            }

            // Every line still being sung keeps filling, not only the latest one: a held
            // note outlasts the line break, and two lines may be sung at the same time.
            foreach (var word in row.WordItems)
            {
                Paint(word, time, motion);
            }

            foreach (var word in row.BackgroundItems)
            {
                Paint(word, time, motion);
            }
        }
    }

    /// <summary>Whether a line's own time still covers the song's position: a line that
    /// started keeps filling until its end, even after a later line has started.</summary>
    private static bool IsSung(Row row, double time)
    {
        var end = row.Line.End ?? (row.WordItems.Count > 0 ? row.WordItems[^1].Data.End : row.Line.Time);
        return time >= row.Line.Time && time <= end + 0.05;
    }

    /// <summary>Repaints every word after a seek, so lines ahead are unsung and the lines
    /// already behind are complete, instead of keeping the old line's partial fill.</summary>
    private void RepaintWords(double time)
    {
        foreach (var row in _rows)
        {
            var started = time >= row.Line.Time;
            foreach (var word in row.WordItems)
            {
                Paint(word, time, _motion, started);
            }

            foreach (var word in row.BackgroundItems)
            {
                Paint(word, time, _motion, started);
            }
        }
    }

    /// <summary>
    /// The three dots of a gap: before the first line, or after the line that just ended,
    /// while the gap lasts. Only one gap is ever on screen, as on the Mac. The gap is
    /// measured from the line's end; measuring from its start put dots after every line.
    /// </summary>
    private void PaintInterlude(double time)
    {
        Row? active = null;
        var leading = false;
        var progress = 0.0;

        var current = Lyrics.IndexOf(time, _lyricLines);
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
                active.Opacity = _idleNear;
            }
        }

        foreach (var row in _rows)
        {
            if (row.DotsHost is null || ReferenceEquals(row, active))
            {
                continue;
            }

            if (row.DotsHost.Visibility != Visibility.Collapsed)
            {
                row.DotsHost.Visibility = Visibility.Collapsed;
            }
        }

        if (active?.DotsHost is { } host)
        {
            host.Visibility = Visibility.Visible;
            FillDots(active.Dots, progress);
        }
        else if (current is { } index && index < _rows.Count)
        {
            // Coming back out of a gap (a seek, a replay) lights the line again.
            _rows[index].Opacity = 1;
        }

        if (_leadingDots is not null)
        {
            var show = leading ? Visibility.Visible : Visibility.Collapsed;
            if (_leadingDots.Visibility != show)
            {
                _leadingDots.Visibility = show;
            }

            if (leading)
            {
                FillDots(_leadingDotItems, progress);
            }
        }
    }

    private static void FillDots(IReadOnlyList<Ellipse> dots, double progress)
    {
        for (var dot = 0; dot < dots.Count; dot++)
        {
            var fill = Math.Clamp(progress * dots.Count - dot, 0, 1);
            var alpha = (byte)(0x4D + (0xFF - 0x4D) * fill);
            dots[dot].Fill = Brush(alpha);
        }
    }

    /// <summary>One word: how much of it is sung, how far it has risen, how much it glows.</summary>
    private void Paint(Word word, double time, bool motion, bool lineStarted = true)
    {
        var data = word.Data;
        var classic = _renderer == LyricsRenderer.Classic;
        var expressive = _renderer == LyricsRenderer.Expressive;
        // Classic lights the whole line; the other looks fill each word as it is sung.
        var fill = classic
            ? (lineStarted ? 1 : 0)
            : Math.Clamp((time - data.Start) / Math.Max(0.001, data.End - data.Start), 0, 1);
        var rise = motion && _renderer == LyricsRenderer.Sweep ? Math.Clamp((time - data.Start) / RiseSeconds, 0, 1) : 0;
        var eased = 1 - Math.Pow(1 - rise, 3); // quick at first, settling gently
        var sustainStrength = motion && !classic ? Math.Clamp((data.End - data.Start - SustainStart) / SustainStart, 0, 1) : 0;
        var sustain = sustainStrength * Math.Sin(Math.PI * fill);

        // The mask runs from -feather to 1, so the word is clear at the start and full at the end.
        var feather = classic ? 0.0 : expressive ? Math.Min(1, _feather * 1.25) : _feather;
        var front = fill * (1 + feather);
        if (Math.Abs(fill - word.PaintedFill) >= 0.0005)
        {
            // An unsung word is wholly grey, including a glyph that overhangs its box on
            // the left (a "j"); otherwise the clamped first stop leaves a white sliver.
            // A fully sung word is wholly white, and only the moving front is a gradient.
            word.PaintedFill = fill;
            var stops = word.Fill.GradientStops;
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

    /// <summary>A step toward a value, with the softness of a spring.</summary>
    private static double Approach(double current, double target)
    {
        var step = (target - current) * 0.18;
        if (Math.Abs(step) < 0.001)
        {
            return target;
        }

        return current + step;
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
        _scroll.ChangeView(null, _tweenStart + (_tweenTarget - _tweenStart) * eased, null);
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
            ? Microsoft.UI.Text.FontWeights.Medium
            : _renderer switch
            {
                LyricsRenderer.Classic => Microsoft.UI.Text.FontWeights.Normal,
                _ => Microsoft.UI.Text.FontWeights.Bold,
            };
        block.FontSize = fontSize;
        block.CharacterSpacing = Tracking;
        block.LineHeight = Math.Round(fontSize * _lineHeightFactor);
        block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
    }

    public void Dispose()
    {
        _tween.Stop();
        _frame.Stop();
    }
}


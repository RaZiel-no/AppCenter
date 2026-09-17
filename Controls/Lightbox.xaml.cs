using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AppCenter.Models;
using AppCenter.Services;

namespace AppCenter.Controls;

/// <summary>
/// One screenshot, large, over the whole window, with the rest of the set a
/// key or a click away. The window behind it goes dark; a click anywhere
/// that is not the picture or a button closes it, as does Escape.
///
/// Opens on the strip's thumbnail straight away - blown up, but there - and
/// swaps in the full-size picture when it lands, so the wait is for
/// sharpness rather than for anything to appear.
/// </summary>
public partial class Lightbox
{
    private static readonly Duration FadeIn = new(TimeSpan.FromMilliseconds(160));
    private static readonly Duration FadeOut = new(TimeSpan.FromMilliseconds(120));

    /// <summary>Where the full-size pictures come from. Set by the window that hosts this.</summary>
    public IconService? Icons { get; set; }

    private IReadOnlyList<Screenshot> _screenshots = [];
    private int _index;

    /// <summary>
    /// The full-size pictures fetched so far this showing, by index. Dropped
    /// on close: at 8MB each they are not worth keeping for a set that may
    /// never be opened again.
    /// </summary>
    private readonly Dictionary<int, ImageSource> _full = [];

    /// <summary>Cancels the fetches of one showing when it closes or moves on.</summary>
    private CancellationTokenSource _showing = new();

    /// <summary>Where the keyboard was before the lightbox took it, to give it back.</summary>
    private IInputElement? _focusBefore;

    /// <summary>
    /// Counts openings and closings, so that a fade-out still running when
    /// the lightbox is shown again cannot finish by putting it away.
    /// </summary>
    private int _generation;

    public Lightbox()
    {
        InitializeComponent();
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    public int Index => _index;

    /// <summary>The set to show and which one to open on. A set of none is nothing to show.</summary>
    public void Show(IReadOnlyList<Screenshot> screenshots, int index)
    {
        if (screenshots.Count == 0)
            return;

        _showing.Cancel();
        _showing = new CancellationTokenSource();
        _full.Clear();

        _screenshots = screenshots;
        _focusBefore = Keyboard.FocusedElement;

        _generation++;

        // Whatever fade was running is over: shown means shown.
        var wasOpen = IsOpen;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        Visibility = Visibility.Visible;

        MoveTo(Math.Clamp(index, 0, screenshots.Count - 1));
        Focus();

        if (!wasOpen)
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeIn));
    }

    public void Close()
    {
        if (!IsOpen)
            return;

        _showing.Cancel();
        _full.Clear();

        var generation = ++_generation;
        var fade = new DoubleAnimation(1, 0, FadeOut);
        fade.Completed += (_, _) =>
        {
            // Shown again while fading: that showing owns the control now.
            if (generation != _generation)
                return;

            Visibility = Visibility.Collapsed;
            Picture.Source = null;
            BeginAnimation(OpacityProperty, null);
        };
        BeginAnimation(OpacityProperty, fade);

        // Back to the thumbnail that opened it, so Enter opens it again and
        // Tab carries on from where it was.
        if (_focusBefore is not null)
            Keyboard.Focus(_focusBefore);
        _focusBefore = null;
    }

    public void Next() => MoveTo(_index + 1);

    public void Previous() => MoveTo(_index - 1);

    private void MoveTo(int index)
    {
        if (index < 0 || index >= _screenshots.Count)
            return;

        _index = index;
        var screenshot = _screenshots[index];

        Picture.Source = _full.TryGetValue(index, out var full) ? full : screenshot.Preview;
        Caption.Text = _screenshots.Count > 1 ? $"{index + 1} / {_screenshots.Count}" : string.Empty;
        AutomationProperties.SetName(Picture, screenshot.Label);

        // Off at the ends rather than wrapping round: an arrow that goes dim
        // says where the set ends, and a set of one has nowhere to go at all.
        PrevButton.IsEnabled = index > 0;
        NextButton.IsEnabled = index < _screenshots.Count - 1;
        PrevButton.Visibility = NextButton.Visibility =
            _screenshots.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        if (full is null)
            _ = SharpenAsync(index, _showing.Token);
    }

    /// <summary>
    /// Fetches the full-size picture for one index and, if that one is still
    /// the one showing, puts it up. Then the next one along, quietly, so that
    /// pressing Right finds it already sharp.
    /// </summary>
    private async Task SharpenAsync(int index, CancellationToken ct)
    {
        if (Icons is null)
            return;

        try
        {
            var full = await FetchFullAsync(index, ct);
            if (full is not null && _index == index && IsOpen)
                Picture.Source = full;

            if (index + 1 < _screenshots.Count)
                await FetchFullAsync(index + 1, ct);
        }
        catch (OperationCanceledException)
        {
            // Closed, or moved on.
        }
        catch (Exception)
        {
            // The thumbnail stays up; it is a picture of the right thing.
        }
    }

    private async Task<ImageSource?> FetchFullAsync(int index, CancellationToken ct)
    {
        if (_full.TryGetValue(index, out var cached))
            return cached;

        var screenshot = _screenshots[index];
        var full = await Icons!.GetFullScreenshotAsync(screenshot.Url, screenshot.CacheKey, ct);

        if (full is not null && !ct.IsCancellationRequested)
            _full[index] = full;

        return full;
    }

    // ---------------------------------------------------------------
    // Input
    // ---------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || !IsOpen)
            return;

        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Left:
            case Key.PageUp:
                Previous();
                break;
            case Key.Right:
            case Key.PageDown:
            case Key.Space:
                Next();
                break;
            case Key.Home:
                MoveTo(0);
                break;
            case Key.End:
                MoveTo(_screenshots.Count - 1);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnScrimClicked(object sender, MouseButtonEventArgs e)
    {
        Close();
        e.Handled = true;
    }

    /// <summary>A click on the picture is not a click outside it.</summary>
    private void OnPictureClicked(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnPrevClicked(object sender, RoutedEventArgs e) => Previous();

    private void OnNextClicked(object sender, RoutedEventArgs e) => Next();

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AppCenter.Models;
using AppCenter.Services;

namespace AppCenter.Views;

public partial class GamesView : PageView
{
    /// <summary>One position indicator under the carousel.</summary>
    private sealed record Dot(bool Active);

    /// <summary>What the carousel shows at one moment: the slide, and the two
    /// neighbours peeking in beside it.</summary>
    private sealed record Strip(AppPackage Previous, AppPackage Current, AppPackage Next);

    /// <summary>How long a slide sits before the next one takes over.</summary>
    private static readonly TimeSpan AutoAdvanceInterval = TimeSpan.FromSeconds(7);

    private static readonly Duration TransitionDuration = new(TimeSpan.FromSeconds(0.35));

    /// <summary>How far a frame drifts sideways as it hands over. Short on
    /// purpose: enough to say which way the carousel moved, not so far that the
    /// slide looks like it is being thrown off the page.</summary>
    private const double TransitionDrift = 56;

    private readonly List<AppPackage> _carousel;
    private readonly List<AppPackage> _topRated;
    private readonly DispatcherTimer _autoAdvance;

    private int _index;

    public GamesView()
    {
        InitializeComponent();

        _carousel = CatalogService.Section("carousel");
        _topRated = CatalogService.Section("games");

        Cards.ItemsSource = _topRated;
        Cards.ItemClick += package => Host.ShowDetail(package);

        _autoAdvance = new DispatcherTimer { Interval = AutoAdvanceInterval };
        _autoAdvance.Tick += (_, _) => Advance(1);

        Loaded += (_, _) =>
        {
            if (_carousel.Count > 1)
                _autoAdvance.Start();
        };

        Unloaded += (_, _) => _autoAdvance.Stop();

        ShowSlide(0);
    }

    public override Task LoadAsync()
    {
        Host.Icons.BeginLoad(_topRated, Dispatcher);
        Host.Icons.BeginLoadScreenshots(_carousel, Dispatcher);
        return Task.CompletedTask;
    }

    /// <summary>Puts the slide at <see cref="_index"/> on screen. A direction of
    /// 0 places it outright, which is what the first frame wants; anything else
    /// is the way the carousel just moved, and animates.</summary>
    private void ShowSlide(int direction)
    {
        if (_carousel.Count == 0)
        {
            CurrentStrip.Visibility = Visibility.Collapsed;
            return;
        }

        var strip = new Strip(
            _carousel[Wrap(_index - 1)],
            _carousel[_index],
            _carousel[Wrap(_index + 1)]);

        if (direction == 0)
            CurrentStrip.Content = strip;
        else
            Transition(strip, direction);

        Dots.ItemsSource = Enumerable
            .Range(0, _carousel.Count)
            .Select(i => new Dot(i == _index))
            .ToList();
    }

    /// <summary>Hands what is on screen down to the outgoing layer and brings
    /// the new frame in over the top of it, both drifting the way the carousel
    /// moved while one fades out and the other fades in.</summary>
    private void Transition(Strip strip, int direction)
    {
        // Where the incoming frame had got to, so a second click part way
        // through the first transition carries on from there rather than
        // snapping back to full opacity.
        var handoverOpacity = CurrentStrip.Opacity;
        var handoverOffset = ((TranslateTransform)CurrentStrip.RenderTransform).X;

        OutgoingStrip.Content = CurrentStrip.Content;
        CurrentStrip.Content = strip;

        var drift = direction * TransitionDrift;

        Animate(OutgoingStrip, handoverOpacity, 0, handoverOffset, handoverOffset - drift);
        Animate(CurrentStrip, 0, 1, drift, 0);
    }

    private static void Animate(ContentControl layer, double fromOpacity, double toOpacity, double fromX, double toX)
    {
        // One easing instance for both properties: they are the same movement
        // seen two ways, and would read as two if they were paced differently.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        layer.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(fromOpacity, toOpacity, TransitionDuration) { EasingFunction = ease });

        layer.RenderTransform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(fromX, toX, TransitionDuration) { EasingFunction = ease });
    }

    private int Wrap(int index)
    {
        var count = _carousel.Count;
        return count == 0 ? 0 : (index % count + count) % count;
    }

    private void Advance(int delta)
    {
        if (_carousel.Count == 0)
            return;

        _index = Wrap(_index + delta);
        ShowSlide(delta);
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e)
    {
        RestartAutoAdvance();
        Advance(-1);
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        RestartAutoAdvance();
        Advance(1);
    }

    /// <summary>Manual navigation resets the timer, so a slide the user just
    /// picked is not yanked away a moment later.</summary>
    private void RestartAutoAdvance()
    {
        if (!_autoAdvance.IsEnabled)
            return;

        _autoAdvance.Stop();
        _autoAdvance.Start();
    }
}

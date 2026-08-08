using System.Windows;
using System.Windows.Threading;
using AppCenter.Models;
using AppCenter.Services;

namespace AppCenter.Views;

public partial class GamesView : PageView
{
    /// <summary>One position indicator under the carousel.</summary>
    private sealed record Dot(bool Active);

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

        _autoAdvance = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _autoAdvance.Tick += (_, _) => Advance(1);

        Loaded += (_, _) =>
        {
            if (_carousel.Count > 1)
                _autoAdvance.Start();
        };

        Unloaded += (_, _) => _autoAdvance.Stop();

        ShowSlide();
    }

    public override Task LoadAsync()
    {
        Host.Icons.BeginLoad(_topRated, Dispatcher);
        Host.Icons.BeginLoadScreenshots(_carousel, Dispatcher);
        return Task.CompletedTask;
    }

    private void ShowSlide()
    {
        if (_carousel.Count == 0)
        {
            CurrentSlide.Visibility = Visibility.Collapsed;
            return;
        }

        CurrentSlide.Content = _carousel[_index];
        PeekPrevious.Content = _carousel[Wrap(_index - 1)];
        PeekNext.Content = _carousel[Wrap(_index + 1)];

        Dots.ItemsSource = Enumerable
            .Range(0, _carousel.Count)
            .Select(i => new Dot(i == _index))
            .ToList();
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
        ShowSlide();
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

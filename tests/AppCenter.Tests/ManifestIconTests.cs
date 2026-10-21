using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// A manifest may declare several icons - by size, by file type, by the
/// background each is drawn for - and the hunt tries at most two of them,
/// best first. These pin down what "best" means. The API that hands the
/// icons over is not called here; the machine tests exercise that.
/// </summary>
public class ManifestIconTests
{
    private const string Cdn = "https://cdn.winget.microsoft.com/icons/";

    [Fact]
    public void Prefers_the_largest_icon_for_any_background()
    {
        IReadOnlyList<WingetIcon> icons =
        [
            new(Cdn + "small.ico", WingetIconType.Ico, 48, WingetIconTheme.Default),
            new(Cdn + "large.ico", WingetIconType.Ico, 256, WingetIconTheme.Default),
            new(Cdn + "medium.ico", WingetIconType.Ico, 96, WingetIconTheme.Default),
        ];

        Assert.Equal([Cdn + "large.ico", Cdn + "medium.ico"], IconService.ManifestCandidates(icons));
    }

    [Fact]
    public void Puts_an_icon_drawn_for_one_background_after_one_for_any()
    {
        IReadOnlyList<WingetIcon> icons =
        [
            new(Cdn + "light.png", WingetIconType.Png, 256, WingetIconTheme.Light),
            new(Cdn + "dark.png", WingetIconType.Png, 256, WingetIconTheme.Dark),
            new(Cdn + "plain.png", WingetIconType.Png, 48, WingetIconTheme.Unknown),
        ];

        // Dark ahead of light: the look the app opens with.
        Assert.Equal([Cdn + "plain.png", Cdn + "dark.png"], IconService.ManifestCandidates(icons));
    }

    [Fact]
    public void Puts_a_custom_size_and_a_jpeg_last()
    {
        IReadOnlyList<WingetIcon> icons =
        [
            new(Cdn + "photo.jpg", WingetIconType.Jpeg, 256, WingetIconTheme.Default),
            new(Cdn + "custom.png", WingetIconType.Png, 0, WingetIconTheme.Default),
            new(Cdn + "sixteen.ico", WingetIconType.Ico, 16, WingetIconTheme.Default),
        ];

        Assert.Equal([Cdn + "sixteen.ico", Cdn + "custom.png"], IconService.ManifestCandidates(icons));
    }

    [Fact]
    public void Tries_at_most_two_and_each_url_once()
    {
        IReadOnlyList<WingetIcon> icons =
        [
            new(Cdn + "same.ico", WingetIconType.Ico, 256, WingetIconTheme.Default),
            new(Cdn + "SAME.ico", WingetIconType.Ico, 96, WingetIconTheme.Default),
            new(Cdn + "other.ico", WingetIconType.Ico, 48, WingetIconTheme.Default),
            new(Cdn + "third.ico", WingetIconType.Ico, 32, WingetIconTheme.Default),
        ];

        Assert.Equal([Cdn + "same.ico", Cdn + "other.ico"], IconService.ManifestCandidates(icons));
    }

    [Fact]
    public void Leaves_out_what_cannot_be_fetched()
    {
        IReadOnlyList<WingetIcon> icons =
        [
            new("file:///C:/icon.ico", WingetIconType.Ico, 256, WingetIconTheme.Default),
            new("not a url", WingetIconType.Ico, 256, WingetIconTheme.Default),
            new("", WingetIconType.Ico, 256, WingetIconTheme.Default),
        ];

        Assert.Empty(IconService.ManifestCandidates(icons));
        Assert.Empty(IconService.ManifestCandidates([]));
    }
}

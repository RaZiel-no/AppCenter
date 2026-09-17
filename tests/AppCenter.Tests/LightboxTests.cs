using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AppCenter.Controls;
using AppCenter.Models;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// The lightbox is a set and a position in it. These pin down the position:
/// where it opens, that it stops at the ends rather than wrapping, what the
/// arrows say about that, and that closing puts everything away. Built on an
/// STA thread like any other control; no pictures are fetched, since there
/// is no icon service wired in and the thumbnail is shown regardless.
/// </summary>
public class LightboxTests
{
    private static ImageSource Pixel()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static IReadOnlyList<Screenshot> Set(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new Screenshot(Pixel(), $"https://x/{i}", $"key{i}", $"Screenshot {i + 1}"))
            .ToList();

    private static Lightbox Open(int count, int index)
    {
        var lightbox = new Lightbox();
        lightbox.Show(Set(count), index);
        return lightbox;
    }

    [Fact]
    public void Opens_on_the_screenshot_that_was_clicked()
    {
        Sta.Run(() =>
        {
            var lightbox = Open(3, 1);

            Assert.True(lightbox.IsOpen);
            Assert.Equal(1, lightbox.Index);
        });
    }

    [Fact]
    public void Stops_at_the_ends_rather_than_wrapping()
    {
        Sta.Run(() =>
        {
            var lightbox = Open(3, 2);

            lightbox.Next();
            Assert.Equal(2, lightbox.Index);

            lightbox.Previous();
            lightbox.Previous();
            lightbox.Previous();
            Assert.Equal(0, lightbox.Index);
        });
    }

    [Fact]
    public void The_arrows_say_which_way_is_left_to_go()
    {
        Sta.Run(() =>
        {
            var lightbox = Open(3, 0);

            Assert.False(lightbox.PrevButton.IsEnabled);
            Assert.True(lightbox.NextButton.IsEnabled);

            lightbox.Next();
            Assert.True(lightbox.PrevButton.IsEnabled);
            Assert.True(lightbox.NextButton.IsEnabled);

            lightbox.Next();
            Assert.True(lightbox.PrevButton.IsEnabled);
            Assert.False(lightbox.NextButton.IsEnabled);
        });
    }

    [Fact]
    public void A_set_of_one_has_no_arrows_and_no_count()
    {
        Sta.Run(() =>
        {
            var lightbox = Open(1, 0);

            Assert.Equal(Visibility.Collapsed, lightbox.PrevButton.Visibility);
            Assert.Equal(Visibility.Collapsed, lightbox.NextButton.Visibility);
            Assert.Equal(string.Empty, lightbox.Caption.Text);
        });
    }

    [Fact]
    public void An_index_off_the_end_opens_on_the_nearest_screenshot()
    {
        Sta.Run(() =>
        {
            Assert.Equal(2, Open(3, 99).Index);
            Assert.Equal(0, Open(3, -5).Index);
        });
    }

    [Fact]
    public void Nothing_to_show_stays_closed()
    {
        Sta.Run(() =>
        {
            var lightbox = new Lightbox();
            lightbox.Show([], 0);

            Assert.False(lightbox.IsOpen);
        });
    }

    [Fact]
    public void Shows_the_thumbnail_straight_away()
    {
        Sta.Run(() =>
        {
            var set = Set(2);
            var lightbox = new Lightbox();
            lightbox.Show(set, 1);

            // No icon service is wired in, so nothing sharper can arrive; the
            // strip's copy is what is up, and it is up at once.
            Assert.Same(set[1].Preview, lightbox.Picture.Source);
            Assert.Equal("2 / 2", lightbox.Caption.Text);
        });
    }

    [Fact]
    public void Closing_is_not_instant_but_is_final()
    {
        Sta.Run(() =>
        {
            var lightbox = Open(2, 0);
            lightbox.Close();

            // The fade runs on the dispatcher; without one pumping, the
            // visibility flips when the animation completes, which is never
            // here. What can be checked is that Close is idempotent and that
            // a fresh Show afterwards takes.
            lightbox.Close();
            lightbox.Show(Set(2), 1);
            Assert.True(lightbox.IsOpen);
            Assert.Equal(1, lightbox.Index);
        });
    }
}

using System.Text;
using AppCenter.Services;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// The favicon a modern site declares is as likely to be an SVG as a bitmap,
/// and WPF cannot decode one. These pin down what the SVG path promises: that
/// what comes out is a bitmap at icon size, that an SVG which is only a
/// wrapper around a PNG - a real site's favicon, not a contrivance - is seen
/// through, and that the sniff which routes bytes to it is not fooled by the
/// HTML page a soft 404 serves from an .svg path.
/// </summary>
public class SvgIconTests
{
    private const string Star =
        """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
          <polygon points="50,5 61,38 98,38 68,59 79,92 50,71 21,92 32,59 2,38 39,38" fill="#5aa9dc"/>
        </svg>
        """;

    // A 2x2 opaque red PNG, the smallest thing that is still a picture.
    private const string RedPixels =
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAEklEQVR4nGP4z8DwHwyBFAMDACjdBf2+Xi8CAAAAAElFTkSuQmCC";

    [Fact]
    public void Draws_an_svg_at_icon_size()
    {
        var icon = IconService.RasterizeSvg(Encoding.UTF8.GetBytes(Star));

        Assert.NotNull(icon);
        Assert.True(icon.IsFrozen);
        Assert.InRange(Math.Max(icon.PixelWidth, icon.PixelHeight), 100, 160);
    }

    [Fact]
    public void Sees_through_an_svg_that_only_wraps_a_png()
    {
        // apps.ankiweb.net's logo.svg is exactly this: one <image> carrying a
        // data URI, which a browser draws and a plain bitmap decoder rejects.
        var wrapper =
            $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="2" height="2">
              <image xlink:href="data:image/png;base64,{RedPixels}" width="2" height="2"/>
            </svg>
            """;

        var icon = IconService.RasterizeSvg(Encoding.UTF8.GetBytes(wrapper));

        Assert.NotNull(icon);
        Assert.True(icon.PixelWidth >= 100);
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("\n\n  <svg/>")]
    [InlineData("﻿<svg/>")]
    public void Recognises_an_svg_by_its_first_bytes(string text)
    {
        Assert.True(IconService.LooksLikeSvg(Encoding.UTF8.GetBytes(text)));
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><head><title>Not found</title></head></html>")]
    [InlineData("<html><body>404</body></html>")]
    [InlineData("PNG\r\n\n")]
    [InlineData("")]
    public void Does_not_mistake_anything_else_for_one(string text)
    {
        Assert.False(IconService.LooksLikeSvg(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Gives_nothing_for_an_svg_that_draws_nothing()
    {
        var empty = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"/>";

        Assert.Null(IconService.RasterizeSvg(Encoding.UTF8.GetBytes(empty)));
    }

    [Fact]
    public void Gives_nothing_for_a_page_that_is_not_an_svg_at_all()
    {
        var html = "<!DOCTYPE html><html><body>Not found</body></html>";

        Assert.Null(IconService.RasterizeSvg(Encoding.UTF8.GetBytes(html)));
    }
}

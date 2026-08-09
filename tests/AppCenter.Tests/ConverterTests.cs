using System.Globalization;
using System.Windows;
using AppCenter.Controls;
using Xunit;

namespace AppCenter.Tests;

public class ConverterTests
{
    private static readonly EmptyStringToCollapsedConverter Collapse = new();

    [Theory]
    [InlineData("Restart the app to finish")]
    [InlineData("0")]
    public void Shows_an_element_that_has_something_to_say(string value)
    {
        Assert.Equal(Visibility.Visible, Convert(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Collapses_one_that_does_not(string? value)
    {
        // A DataTrigger with Value="" compares the bound value against the
        // literal without conversion, so the empty case silently fails to match
        // and the badge shows on every row. This is why there is a converter.
        Assert.Equal(Visibility.Collapsed, Convert(value));
    }

    [Fact]
    public void Collapses_anything_that_is_not_a_string_at_all()
    {
        Assert.Equal(Visibility.Collapsed, Convert(42));
    }

    [Fact]
    public void Refuses_to_convert_back()
    {
        // Nothing binds two-way through it, and a silent wrong answer here
        // would be worse than a loud one.
        Assert.Throws<NotSupportedException>(() =>
            Collapse.ConvertBack(Visibility.Visible, typeof(string), null, CultureInfo.InvariantCulture));

        Assert.Throws<NotSupportedException>(() =>
            new IconKeyConverter().ConvertBack(null, typeof(string), null, CultureInfo.InvariantCulture));
    }

    private static object Convert(object? value) =>
        Collapse.Convert(value, typeof(Visibility), null, CultureInfo.InvariantCulture);
}

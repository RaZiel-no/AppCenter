using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using AppCenter.Controls;
using Xunit;

namespace AppCenter.Tests;

/// <summary>
/// A click on a row's Update button has to be traced back to the button that
/// raised it. RoutedEventArgs.Source cannot do it - as a Click bubbles out of an
/// item template WPF re-maps Source to the ItemsControl - and OriginalSource
/// points at the innermost element inside the button, hence the walk.
/// </summary>
public class TreeSearchTests
{
    [Fact]
    public void Finds_the_ancestor_it_was_asked_for()
    {
        Sta.Run(() =>
        {
            var button = new Button();
            var panel = new StackPanel();
            var border = new Border { Child = panel };
            panel.Children.Add(button);

            Assert.Same(border, TreeSearch.FindAncestor<Border>(button));
        });
    }

    [Fact]
    public void Finds_the_nearest_one_when_there_are_two()
    {
        Sta.Run(() =>
        {
            var button = new Button();
            var inner = new Border { Child = button };
            var outer = new Border { Child = inner };

            Assert.Same(inner, TreeSearch.FindAncestor<Border>(button));
            Assert.NotSame(outer, TreeSearch.FindAncestor<Border>(button));
        });
    }

    [Fact]
    public void Takes_the_node_itself_when_it_is_already_the_type()
    {
        Sta.Run(() =>
        {
            var button = new Button();
            new Border { Child = button };

            // A click that lands on the button's own bounds rather than on
            // anything inside it starts here.
            Assert.Same(button, TreeSearch.FindAncestor<Button>(button));
        });
    }

    [Fact]
    public void Comes_back_with_nothing_when_there_is_no_such_ancestor()
    {
        Sta.Run(() =>
        {
            var button = new Button();
            new Border { Child = button };

            // Which is what lets a click on the row itself be told apart from a
            // click on one of its buttons.
            Assert.Null(TreeSearch.FindAncestor<ListBox>(button));
        });
    }

    [Fact]
    public void Comes_back_with_nothing_when_handed_nothing()
    {
        Assert.Null(TreeSearch.FindAncestor<Button>(null));
    }

    [Fact]
    public void Crosses_from_content_that_is_not_visual()
    {
        Sta.Run(() =>
        {
            var run = new Run("Update");
            var text = new TextBlock();
            text.Inlines.Add(run);
            var border = new Border { Child = text };

            // A Run is a content element with no visual parent of its own, so
            // the walk has to change trees to get past it - which is exactly
            // what the innermost hit element of a real click can be.
            Assert.Same(border, TreeSearch.FindAncestor<Border>(run));
        });
    }
}

using System.Windows;
using AppCenter.Models;

namespace AppCenter.Controls;

/// <summary>
/// A dark confirmation prompt, shown before anything that actually changes
/// the machine. The stock MessageBox is light-themed and would break the
/// look of the app entirely.
/// </summary>
public partial class ConfirmDialog : Window
{
    private Answer _answer = Answer.Cancel;

    private ConfirmDialog()
    {
        InitializeComponent();
    }

    public static bool Show(Window owner, string title, string message, string confirmLabel) =>
        Choose(owner, title, message, confirmLabel, null) == Answer.Confirm;

    /// <summary>
    /// The same prompt with a second way to go ahead, between Cancel and the
    /// confirm button, when there is one. Closing the window any other way is
    /// Cancel.
    /// </summary>
    public static Answer Choose(Window owner, string title, string message, string confirmLabel, string? alternativeLabel)
    {
        var dialog = new ConfirmDialog
        {
            Owner = owner,
        };

        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmLabel;

        if (alternativeLabel is not null)
        {
            dialog.AlternativeButton.Content = alternativeLabel;
            dialog.AlternativeButton.Visibility = Visibility.Visible;
        }

        dialog.ShowDialog();
        return dialog._answer;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => Finish(Answer.Confirm);

    private void OnAlternative(object sender, RoutedEventArgs e) => Finish(Answer.Alternative);

    private void OnCancel(object sender, RoutedEventArgs e) => Finish(Answer.Cancel);

    private void Finish(Answer answer)
    {
        _answer = answer;
        DialogResult = answer != Answer.Cancel;
        Close();
    }
}

using System.Windows;

namespace AppCenter.Controls;

/// <summary>
/// A dark confirmation prompt, shown before anything that actually changes
/// the machine. The stock MessageBox is light-themed and would break the
/// look of the app entirely.
/// </summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog()
    {
        InitializeComponent();
    }

    public static bool Show(Window owner, string title, string message, string confirmLabel)
    {
        var dialog = new ConfirmDialog
        {
            Owner = owner,
        };

        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmLabel;

        return dialog.ShowDialog() == true;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

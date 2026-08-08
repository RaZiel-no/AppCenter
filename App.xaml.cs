using System.Windows;
using System.Windows.Threading;
using AppCenter.Services;

namespace AppCenter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;

        // Ahead of base.OnStartup, which is what creates MainWindow: the
        // palette has to be right before the first pixel is drawn.
        ThemeService.Apply(SettingsService.Current.Theme);

        base.OnStartup(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.Message,
            "App Center hit an unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}

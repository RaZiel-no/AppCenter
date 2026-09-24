using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AppCenter.Services;

/// <summary>
/// What the first screen is going to need, started the moment the process
/// does, on threads the UI thread is not using.
///
/// The UI thread has a fixed amount of work between the process starting
/// and the first frame - parse the resources, build the window, lay it out
/// - and none of it can move. What can move is everything it would
/// otherwise stop for along the way. Some of that is the app's own: the
/// settings file, the catalogue, the icons already on disk. Most of it is
/// WPF's. The composition engine, the imaging codecs and the font system
/// each start on first use, on whichever thread uses them first, and each
/// is process-wide once started; used first from here, they are started
/// by the time the UI thread asks, on a core it was not using.
///
/// The composition engine wants a thread with a Dispatcher of its own -
/// the way a splash screen on a second UI thread gets one - so that is
/// what it is given, and the thread stays until the window has its own
/// connection, since the engine shuts down again with the last one.
/// </summary>
public static class Warmup
{
    /// <summary>
    /// The sidebar logo, decoded off the UI thread. Null if that failed. It is
    /// the first bitmap decoded in the process, so it is also what loads the
    /// imaging codecs, here rather than on the UI thread.
    /// </summary>
    public static Task<BitmapSource?> Logo { get; private set; } = Task.FromResult<BitmapSource?>(null);

    /// <summary>
    /// The one icon service, created here so its cache can be read before the
    /// window exists. Whoever asks first builds it and the next waits, so
    /// there is one whether or not anything warmed up.
    /// </summary>
    public static IconService Icons => IconsOnce.Value;

    private static readonly Lazy<IconService> IconsOnce = new(() => new IconService());
    private static Dispatcher? _engine;

    public static void Begin()
    {
        var engine = new Thread(RunEngineThread) { IsBackground = true, Name = "warm-up" };
        engine.SetApartmentState(ApartmentState.STA);
        engine.Start();

        Logo = Task.Run(() => IconService.AppIcon);

        SettingsService.Preload();
        CatalogService.Preload();

        // The Explore cards' icons, from the disk cache, so they are in hand
        // before the cards are built rather than arriving one by one after
        // the first frame. The service memoises each answer by package; the
        // page asks for the same ones and gets them at once.
        _ = Task.Run(() =>
        {
            foreach (var package in CatalogService.Section("explore"))
                _ = Icons.GetIconAsync(package);
        });
    }

    /// <summary>
    /// Lets the engine thread go. For once the window is on screen: its own
    /// connection keeps the engine alive from here on.
    /// </summary>
    public static void Release()
    {
        _engine?.BeginInvokeShutdown(DispatcherPriority.Background);
        _engine = null;
    }

    private static void RunEngineThread()
    {
        _engine = Dispatcher.CurrentDispatcher;

        try
        {
            // Setting any property on a Visual connects the thread to the
            // composition engine, and the first connection starts it: the
            // render thread, the graphics device, the tier check. WPF waits
            // for the answer, which is the wait the UI thread is spared.
            new ContainerVisual().Opacity = 1;

            // The font system: the DirectWrite factory and the system font
            // collection, both process-wide, both loaded by the first line of
            // text anyone lays out.
            var typeface = new Typeface(
                new FontFamily("Ubuntu Sans, Ubuntu, Segoe UI Variable Text, Segoe UI"),
                FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            _ = new FormattedText(
                "App Center", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, 14, Brushes.White, 1.0).Width;
        }
        catch (Exception)
        {
            // Nothing here is needed for correctness; the UI thread does
            // whatever was not done when it gets there.
        }

        Dispatcher.Run();
    }
}

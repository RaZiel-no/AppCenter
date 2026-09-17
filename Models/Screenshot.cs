using System.Windows.Media;

namespace AppCenter.Models;

/// <summary>
/// One screenshot on an app's page: the thumbnail already decoded for the
/// strip, and what is needed to fetch the full-size picture when the
/// lightbox asks for it.
/// </summary>
/// <param name="Preview">The strip's bitmap, decoded to carousel width.</param>
/// <param name="Url">Where the picture lives; the lightbox fetches it again at full size.</param>
/// <param name="CacheKey">The key the preview was cached under; the full-size copy hangs off it.</param>
/// <param name="Label">What a screen reader calls it.</param>
public sealed record Screenshot(ImageSource Preview, string Url, string CacheKey, string Label);

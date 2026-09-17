using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppCenter.Services;

/// <summary>
/// The (de)serialisers for the app's own files, generated at build time
/// rather than worked out by reflection on first use - which is a visible
/// slice of a launch, since the settings are read before the first pixel
/// and the catalogue right after.
///
/// One set of options serves both files: the catalogue is hand-written, so
/// comments and trailing commas are tolerated; the settings are written by
/// the app, indented so they can be read.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    WriteIndented = true)]
[JsonSerializable(typeof(CatalogRoot))]
[JsonSerializable(typeof(Settings))]
internal partial class AppJsonContext : JsonSerializerContext;

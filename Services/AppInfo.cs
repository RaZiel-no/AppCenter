using System.IO;
using System.Reflection;

namespace AppCenter.Services;

/// <summary>
/// What this build knows about itself: its version, where it is published,
/// and whether the copy that is running was put there by the installer.
/// </summary>
public static class AppInfo
{
    /// <summary>The GitHub repository releases are published from.</summary>
    public const string RepositoryOwner = "RaZiel-no";
    public const string RepositoryName = "AppCenter";
    public const string RepositoryUrl = $"https://github.com/{RepositoryOwner}/{RepositoryName}";

    /// <summary>
    /// App Center's own winget package id - deploy.bat's PACKAGE_ID. It is what
    /// an update of the app is keyed by, whichever way it arrives.
    /// </summary>
    public const string PackageId = "ArnsteinSkara.AppCenter";

    /// <summary>
    /// The number deploy.bat stamped on this build. Informational version is
    /// asked for first because that is what <Version> in the csproj becomes
    /// verbatim - AssemblyVersion is padded out to four parts, so 1.0.4 would
    /// otherwise read as 1.0.4.0 and match nothing the user was given.
    /// </summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>
    /// True when the running exe sits in a folder the installer made: Inno
    /// Setup leaves its uninstaller beside what it installs. The portable zip
    /// has no such thing, and no installer to run over it.
    /// </summary>
    public static bool IsInstalledCopy { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    private static string ReadVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // A source-linked build appends "+<commit>"; the release number is the
        // part in front of it.
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+')[0];

        return assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }
}

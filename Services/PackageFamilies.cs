using System.Text.RegularExpressions;
using AppCenter.Models;

namespace AppCenter.Services;

/// <summary>
/// Folds the installed list into the things a person thinks of as installed.
///
/// `winget list` prints one row per install, and on a Windows machine that is
/// a long way from one row per thing: the Visual C++ redistributables alone are
/// eight rows across four years and two architectures, every .NET SDK sits
/// beside the one before it, and the Windows App Runtime keeps a copy of each
/// major version. None of that is wrong - side-by-side is how those packages
/// work - but a list that shows it row by row buries the two hundred apps the
/// user actually put there under the plumbing they came with.
///
/// So rows are gathered into families and shown as one row each, with the
/// installs listed underneath on request. Two rules decide the family:
///
/// - A winget id names it, once the trailing version, architecture and
///   channel segments are taken off: <c>Microsoft.VCRedist.2010.x64</c> and
///   <c>Microsoft.VCRedist.2015+.x86</c> are both <c>Microsoft.VCRedist</c>,
///   <c>Microsoft.DotNet.SDK.9</c>, <c>.SDK.10</c> and <c>.SDK.Preview</c> are
///   the SDK, and two rows with one id are trivially one family.
/// - Anything winget could not match to a package - an <c>ARP\</c> or
///   <c>MSIX\</c> id - has only its name to go by, so identical names are one
///   family. That is what pairs the x64 and x86 halves of an MSIX framework.
///
/// Nothing is stripped from the middle of an id, and the last two segments
/// always stay, so <c>7zip.7zip</c> is left alone and a sequel that happens to
/// end in a digit is the one case that could fold wrongly.
/// </summary>
public static class PackageFamilies
{
    /// <summary>
    /// The id segments that say which version or build of a family this is
    /// rather than what it is: a number ("10", "2015+"), an architecture, or
    /// a release channel - the preview SDK is the SDK, and Insiders is Code.
    /// </summary>
    private static readonly Regex VersionSegment = new(
        @"^(v?\d+\+?|x64|x86|arm64|arm|amd64|ia64|preview|beta|alpha|rc|nightly|canary|insiders?)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The tail of a name that is version rather than name: "4.8", "v7.00",
    /// "2010", "(x64)", "(64-bit)", "- 10.0.40219", and the dashes and
    /// brackets that introduce them.
    /// </summary>
    private static readonly Regex VersionWord = new(
        @"^(v?\d[\d.]*\+?|\(?(x64|x86|arm64|arm|amd64|64-bit|32-bit)\)?|[-–·:(]+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>The architecture an MSIX package id spells out, if it does.</summary>
    private static readonly Regex MsixArch = new(
        @"_(x64|x86|arm64|arm|neutral)_", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Whether an id is one winget knows a package by, as opposed to the
    /// <c>ARP\…</c> and <c>MSIX\…</c> handles it makes up for installs it could
    /// not match to any source.
    /// </summary>
    public static bool IsWingetId(string id) =>
        id.Length > 0 && !id.Contains('\\');

    /// <summary>What to group this package under.</summary>
    public static string FamilyKey(AppPackage package) =>
        IsWingetId(package.Id)
            ? StripVersionSegments(package.Id)
            : "name:" + Whitespace.Replace(package.Name, " ").Trim().ToLowerInvariant();

    /// <summary>
    /// The id with its trailing version and architecture segments removed.
    /// Never fewer than two segments: publisher and name are the id.
    /// </summary>
    internal static string StripVersionSegments(string id)
    {
        var segments = id.Split('.');
        var keep = segments.Length;

        while (keep > 2 && VersionSegment.IsMatch(segments[keep - 1]))
            keep--;

        return keep == segments.Length ? id : string.Join('.', segments, 0, keep);
    }

    /// <summary>
    /// Gathers packages into families, one <see cref="InstalledGroup"/> each,
    /// in the order the first member of each was met. Members are ordered
    /// newest version first.
    /// </summary>
    public static List<InstalledGroup> Group(IEnumerable<AppPackage> packages)
    {
        var order = new List<string>();
        var families = new Dictionary<string, List<AppPackage>>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in packages)
        {
            var key = FamilyKey(package);

            if (!families.TryGetValue(key, out var members))
            {
                members = [];
                families[key] = members;
                order.Add(key);
            }

            members.Add(package);
        }

        var groups = new List<InstalledGroup>(order.Count);

        foreach (var key in order)
        {
            var members = families[key]
                .OrderByDescending(p => p.Version, VersionOrder.Instance)
                .ToList();

            var title = members.Count == 1 ? members[0].Name : FamilyName(members.Select(p => p.Name));

            foreach (var member in members)
            {
                member.VariantLabel = VariantLabel(member, title, members.Count);

                // What the id adds to the family's - ".2010.x64" is worth a
                // column; the family's own id said again on every row is not.
                member.VariantId = string.Equals(member.DisplayId, key, StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : member.DisplayId;
            }

            groups.Add(new InstalledGroup(key, title, members));
        }

        return groups;
    }

    /// <summary>
    /// What to call a family: the run its members' names share at the start,
    /// cut back to a whole word, followed by whatever words after it every
    /// member still has - and with the version words dropped from both.
    /// "Microsoft Visual C++ 2010 x64 Redistributable - 10.0.40219" and
    /// "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.42.34433"
    /// open with "Microsoft Visual C++ 201", which is "Microsoft Visual C++",
    /// and go on to share "Redistributable": "Microsoft Visual C++
    /// Redistributable". Falls back to the first name when the names have
    /// nothing worth saying in common.
    /// </summary>
    internal static string FamilyName(IEnumerable<string> names)
    {
        var list = names.Select(n => Whitespace.Replace(n, " ").Trim()).Where(n => n.Length > 0).ToList();

        if (list.Count == 0)
            return string.Empty;

        var first = list[0];
        var length = first.Length;

        foreach (var name in list.Skip(1))
        {
            var shared = 0;
            while (shared < length && shared < name.Length
                   && char.ToUpperInvariant(first[shared]) == char.ToUpperInvariant(name[shared]))
                shared++;

            length = shared;
        }

        // Ended mid-word - "…C++ 201" - so back up to the word it was in.
        if (length < first.Length && list.Any(n => length < n.Length && !IsBoundary(n[length])))
        {
            while (length > 0 && !IsBoundary(first[length - 1]))
                length--;
        }

        var prefix = StripVersionWords(first[..length]);

        if (prefix.Length < 3)
            return StripVersionWords(first);

        // The words the first name goes on with that every other name has
        // somewhere too, in the first name's order. Version words are what
        // told the names apart, so they are not part of what they share.
        var rest = first[length..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !VersionWord.IsMatch(word))
            .Where(word => list.Skip(1).All(n => n.Split(' ').Contains(word, StringComparer.OrdinalIgnoreCase)));

        return string.Join(' ', rest.Prepend(prefix));
    }

    private static bool IsBoundary(char c) => char.IsWhiteSpace(c) || c is '.' or '-' or '(' or '_';

    /// <summary>Takes a version off the end of a name, word by word.</summary>
    private static string StripVersionWords(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        while (words.Count > 1 && VersionWord.IsMatch(words[^1]))
            words.RemoveAt(words.Count - 1);

        var joined = string.Join(' ', words).TrimEnd(' ', '.', '-', '–', ':', '(', '_');

        // "Microsoft.UI.Xaml.2" - a dotted name whose last piece is a number.
        if (!joined.Contains(' '))
            joined = StripVersionSegments(joined);

        return joined;
    }

    /// <summary>
    /// What a member is called inside its family: whatever its name adds to
    /// the family's, or its version when the name adds nothing. The family's
    /// name is made of the words every member has, so those words are dropped
    /// wherever they fall - "Microsoft Visual C++ v14 Redistributable (x64)"
    /// under "Microsoft Visual C++ Redistributable" is "v14 (x64)". The x64
    /// and x86 halves of an MSIX package add nothing to each other's name, so
    /// they get the architecture their id spells out.
    /// </summary>
    private static string VariantLabel(AppPackage package, string family, int siblings)
    {
        if (siblings < 2)
            return string.Empty;

        var name = Whitespace.Replace(package.Name, " ").Trim();

        if (name.StartsWith(family, StringComparison.OrdinalIgnoreCase))
            name = name[family.Length..];

        var familyWords = family.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var remainder = string.Join(' ', name
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(word => !familyWords.Contains(word, StringComparer.OrdinalIgnoreCase)))
            .Trim(' ', '-', '–', ':', '·', '.');

        if (remainder.Length > 0)
            return remainder;

        var version = package.Version.Length > 0 ? package.Version : "Unknown version";
        var arch = MsixArch.Match(package.Id);

        return arch.Success ? $"{version} ({arch.Groups[1].Value.ToLowerInvariant()})" : version;
    }
}

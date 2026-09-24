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
///   <c>MSIX\</c> id - has only its name to go by, so names that are the same
///   once their version words are taken out are one family. That is what pairs
///   the x64 and x86 halves of an MSIX framework, and what gathers every
///   "GDR 1050 for SQL Server 2022 (KB5021522) (64-bit)" - one row per
///   cumulative update, each under its own KB number - into one.
///
/// One exception to both, for the runtimes and SDKs that are one thing to the
/// people who use them, however many products they ship as: see
/// <see cref="Suites"/>.
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

    /// <summary>
    /// A word of a name that says which build it is rather than what it is,
    /// wherever in the name it falls: a number or dotted version, a Windows
    /// update's "(KB5021522)", an architecture. Looser than
    /// <see cref="VersionWord"/>, which only looks at the end of a name: this
    /// only decides which rows go together, and the family's title is still
    /// made the careful way.
    /// </summary>
    private static readonly Regex BuildWord = new(
        @"^[(\[]?(v?\d[\d.]*\+?|kb\d+|x64|x86|arm64|arm|amd64|64-bit|32-bit)[)\]]?$|^[-–·:]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A plain number, like the year in "SQL Server 2022".</summary>
    private static readonly Regex PlainNumber = new(@"^\d+$", RegexOptions.Compiled);

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

    /// <summary>
    /// Runtimes and SDKs that belong on one row, though nothing in their ids
    /// or names would put them there. Microsoft's are most of it: the Visual
    /// C++ redistributables, every .NET runtime, host and SDK, the Windows App
    /// Runtime, and the MSIX frameworks Store apps bring with them. Each of
    /// those is already a family of its own, and on a developer's machine the
    /// families alone are still a screenful. So a suite is not a flat list:
    /// its rows are the families and packages its members would have made on
    /// their own, and a family among them opens in turn.
    ///
    /// Python is the same shape at a smaller size: the interpreters, the
    /// launcher and the install manager.
    ///
    /// Runtimes and SDKs only, never everything a publisher makes. Edge,
    /// Visual Studio and Terminal are apps someone chose to install, and
    /// folding them away under the publisher's name would hide the rows
    /// people open Manage to find.
    ///
    /// A list, because the rule cannot be general: plenty of publishers' names
    /// turn up in other publishers' product names. Each entry says where the
    /// name has to be for the package to count.
    /// </summary>
    private static readonly Suite[] Suites =
    [
        // By winget id where there is one, or the package name of an MSIX
        // framework winget could not match; by the name for everything else
        // - the .NET hosts and targeting packs Visual Studio leaves behind.
        new("Microsoft runtimes and SDKs", Publisher: "Microsoft",
            Name: new(@"^Microsoft\b.*(\.NET|Visual C\+\+|Runtime|Redistributable|\bSDK\b|Software Development Kit|Targeting Pack|Developer Pack|Shared Framework|WebView2|DirectX)",
                RegexOptions.IgnoreCase),
            Id: new(@"^(MSIX\\)?Microsoft\.(VCRedist|VCLibs|UI\.Xaml|DotNet|NET\.|WindowsAppRuntime|WinAppRuntime|WindowsSDK|WindowsWDK|DirectX|EdgeWebView2Runtime|OpenJDK)",
                RegexOptions.IgnoreCase)),

        // The interpreters, the launcher and the install manager. Leading
        // only: plenty of other things are "… for Python".
        new("Python", Publisher: "Python",
            Name: new(@"^Python\b", RegexOptions.IgnoreCase),
            Id: new(@"^Python\.", RegexOptions.IgnoreCase)),
    ];

    /// <summary>
    /// One suite: <paramref name="Title"/> is what its row is called, and
    /// <paramref name="Publisher"/> what comes off the front of its members'
    /// names, since the row above them already says it.
    /// </summary>
    private sealed record Suite(string Title, string Publisher, Regex Name, Regex Id);

    /// <summary>The mark a publisher's name is often followed by: "Intel(R)", "Intel®".</summary>
    private static readonly Regex TrademarkMark = new(@"^\s*(\((R|TM|C)\)|®|™|©)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const string SuitePrefix = "suite:";

    /// <summary>The title of the suite this package belongs to, if it is one in <see cref="Suites"/>.</summary>
    internal static string? SuiteOf(AppPackage package) =>
        Suites.FirstOrDefault(s => s.Id.IsMatch(package.Id) || s.Name.IsMatch(package.Name))?.Title;

    /// <summary>What family to group this package under, suites aside.</summary>
    public static string FamilyKey(AppPackage package) =>
        IsWingetId(package.Id)
            ? StripVersionSegments(package.Id)
            : "name:" + NameKey(package.Name);

    /// <summary>
    /// A name with its build words taken out, lower-cased. A name that is
    /// nothing but build words keeps them all: an empty key would make one
    /// family of every such name.
    /// </summary>
    internal static string NameKey(string name)
    {
        var words = Whitespace.Replace(name, " ").Trim().ToLowerInvariant().Split(' ');
        var kept = words.Where(word => !BuildWord.IsMatch(word)).ToArray();

        return string.Join(' ', kept.Length > 0 ? kept : words);
    }

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
    /// in the order the first member of each was met, with each suite
    /// gathered into one group of its own families. Members are ordered
    /// newest version first; a suite's rows by what they are called.
    /// </summary>
    public static List<InstalledGroup> Group(IEnumerable<AppPackage> packages) =>
        Gather(packages, p => SuiteOf(p) is { } suite ? SuitePrefix + suite : FamilyKey(p))
            .Select(g => g.Key.StartsWith(SuitePrefix, StringComparison.Ordinal)
                ? BuildSuite(Suites.First(s => s.Title == g.Key[SuitePrefix.Length..]), g.Members)
                : BuildFamily(g.Key, g.Key, g.Members))
            .ToList();

    /// <summary>Packages bucketed by key, in the order each key was first met.</summary>
    private static List<(string Key, List<AppPackage> Members)> Gather(
        IEnumerable<AppPackage> packages, Func<AppPackage, string> keyOf)
    {
        var order = new List<(string, List<AppPackage>)>();
        var byKey = new Dictionary<string, List<AppPackage>>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in packages)
        {
            var key = keyOf(package);

            if (!byKey.TryGetValue(key, out var members))
            {
                members = [];
                byKey[key] = members;
                order.Add((key, members));
            }

            members.Add(package);
        }

        return order;
    }

    /// <summary>
    /// One family, or one package on its own. <paramref name="familyKey"/> is
    /// what its members' ids are measured against; <paramref name="key"/> is
    /// what the row is known by, which inside a suite has the suite in front
    /// of it so the same family under two suites cannot be confused.
    /// </summary>
    private static InstalledGroup BuildFamily(
        string key, string familyKey, List<AppPackage> packages, string? publisher = null)
    {
        var members = packages.OrderByDescending(p => p.Version, VersionOrder.Instance).ToList();
        var title = members.Count == 1 ? members[0].Name : FamilyName(members.Select(p => p.Name));

        foreach (var member in members)
        {
            member.VariantLabel = VariantLabel(member, title, members.Count);

            // What the id adds to the family's - ".2010.x64" is worth a
            // column; the family's own id said again on every row is not.
            member.VariantId = string.Equals(member.DisplayId, familyKey, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : member.DisplayId;
        }

        // Inside a suite the publisher's name is on the row above; "Visual C++
        // Redistributable" under Microsoft, not "Microsoft Visual C++…".
        var shown = publisher is null ? title : WithoutPublisher(title, publisher);

        return new InstalledGroup(key, shown, members, idText: IdTextOf(familyKey));
    }

    /// <summary>
    /// A suite's packages as one row, which opens to the families and
    /// packages they make on their own. A suite of one family is just that
    /// family: a row that opens to a row that opens says nothing twice.
    /// </summary>
    private static InstalledGroup BuildSuite(Suite suite, List<AppPackage> packages)
    {
        var families = Gather(packages, FamilyKey);

        if (families.Count == 1)
            return BuildFamily(families[0].Key, families[0].Key, families[0].Members);

        var children = families
            .Select(f => BuildFamily($"{SuitePrefix}{suite.Title}/{f.Key}", f.Key, f.Members, suite.Publisher))
            .OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new InstalledGroup(
            SuitePrefix + suite.Title, suite.Title,
            children.SelectMany(c => c.Members).ToList(),
            children, idText: string.Empty);
    }

    /// <summary>
    /// A name with its publisher, and any trademark after it, taken off the
    /// front: "Microsoft® Visual C++ Redistributable" is "Visual C++
    /// Redistributable". Left whole when that would leave nothing but a
    /// version - "Python 3.14.5" is not "3.14.5".
    /// </summary>
    private static string WithoutPublisher(string name, string publisher)
    {
        if (!name.StartsWith(publisher, StringComparison.OrdinalIgnoreCase))
            return name;

        var rest = TrademarkMark.Replace(name[publisher.Length..], string.Empty).Trim(' ', '-', '–', ':', '·');

        return rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(BuildWord.IsMatch) ? name : rest;
    }

    /// <summary>
    /// The id a row shows: the family's, when winget knows it by one. The
    /// handles winget makes up for everything else say nothing to anyone.
    /// </summary>
    private static string IdTextOf(string familyKey) =>
        familyKey.StartsWith("name:", StringComparison.Ordinal) ? string.Empty : familyKey;

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

        // Nothing shared at the start - "X64 Debuggers And Tools" and "X86
        // Debuggers And Tools" part at their first word. What they share after
        // it is still the name, if there is any.
        if (prefix.Length < 3)
        {
            var shared = first
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(word => !VersionWord.IsMatch(word))
                .Where(word => list.Skip(1).All(n => n.Split(' ').Contains(word, StringComparer.OrdinalIgnoreCase)))
                .ToList();

            return shared.Count > 0 ? string.Join(' ', shared) : StripVersionWords(first);
        }

        // The words the first name goes on with that every other name has
        // somewhere too, in the first name's order. Version words are what
        // told the names apart, so they are not part of what they share -
        // except a plain number every one of them has, which is part of the
        // name: the 2022 of "GDR 1050 for SQL Server 2022".
        var rest = first[length..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => list.Skip(1).All(n => n.Split(' ').Contains(word, StringComparer.OrdinalIgnoreCase)))
            .Where(word => !VersionWord.IsMatch(word) || PlainNumber.IsMatch(word));

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
            name = TrademarkMark.Replace(name[family.Length..], string.Empty);

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

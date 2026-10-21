using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using static AppCenter.Services.WinRt;

namespace AppCenter.Services;

/// <summary>
/// What a package's manifest says, as the API hands it over: the fields
/// `winget show` prints, in winget's own terms. <see cref="WingetService.RenderShow"/>
/// writes one out the way winget does.
/// </summary>
internal sealed record WingetManifest(
    string Id,
    string Name,
    string Version,
    string Publisher,
    string PublisherUrl,
    string PublisherSupportUrl,
    string Author,
    string Description,
    string ShortDescription,
    string Homepage,
    string License,
    string LicenseUrl,
    string PrivacyUrl,
    string Copyright,
    string CopyrightUrl,
    string ReleaseNotes,
    string ReleaseNotesUrl,
    string PurchaseUrl,
    string InstallationNotes,
    IReadOnlyList<string> Tags,
    IReadOnlyList<WingetLink> Documentation,
    IReadOnlyList<WingetAgreement> Agreements);

/// <summary>One entry of a manifest's documentation list.</summary>
internal sealed record WingetLink(string Label, string Url);

/// <summary>One agreement a manifest carries: a label, and text or a link or both.</summary>
internal sealed record WingetAgreement(string Label, string Text, string Url);

/// <summary>
/// One installed package as the sources see it: the name and version of the
/// install, the newest version any source has for it - taken as winget takes
/// it, the first of the versions the source lists - and whether winget's own
/// comparison calls that newer than what is installed. Pins and whether an
/// installer fits this machine are left out of that question, which is what
/// `upgrade --include-unknown --include-pinned` leaves out too. Latest and
/// Source are empty for an install no source knows.
/// </summary>
internal sealed record WingetInstalled(string Id, string Name, string Version, string Latest, string Source, bool IsUpdate);

/// <summary>What a manifest's icon is encoded as. The values are the IDL's.</summary>
internal enum WingetIconType
{
    Unknown,
    Jpeg,
    Png,
    Ico,
}

/// <summary>The background a manifest's icon is drawn for. The values are the IDL's.</summary>
internal enum WingetIconTheme
{
    Unknown,
    Default,
    Light,
    Dark,
    HighContrast,
}

/// <summary>
/// One icon a manifest declares: where it is, what it is, the side of the
/// square in pixels - zero when the manifest calls the size custom - and the
/// background it is meant for.
/// </summary>
internal sealed record WingetIcon(string Url, WingetIconType Type, int Pixels, WingetIconTheme Theme);

/// <summary>
/// What the API said, or that it could not say. Unanswered covers the API
/// not being there, breaking, and winget itself having no answer to give;
/// whichever it was, the caller asks winget.exe instead. Answered with null
/// is an answer: there is no such package.
/// </summary>
internal readonly record struct Answer<T>(bool Answered, T Value)
{
    public static readonly Answer<T> Unanswered = default;

    public static Answer<T> Of(T value) => new(true, value);
}

/// <summary>
/// The Windows Package Manager COM API - the same engine as winget.exe, in a
/// server process of its own - for the reads where it is an order of
/// magnitude faster than starting winget: an exact-id lookup is milliseconds
/// here against a quarter of a second per process. Everything that changes
/// the machine stays with winget.exe.
///
/// Reached by hand-written interop (see <see cref="WinRt"/>) through the
/// production out-of-process CLSIDs winget registers. The interfaces below
/// are declared in vtable order from Microsoft.Management.Deployment.winmd as
/// App Installer 1.29 ships it; the IIDs of the parameterised collections
/// were read off the live objects and agree with the signature hash.
///
/// That same winmd ships beside the exe, and has to: the server's objects
/// cross the process boundary by WinRT's metadata-based marshalling, which
/// reads each interface's shape from the metadata, and a process with no
/// package identity can find a third party's metadata only in its own
/// folder. Without the file every call fails to unmarshal, with
/// ERROR_APPMODEL_ERROR_NO_PACKAGE of all things.
///
/// Every read runs to completion on one thread-pool thread, which is in the
/// multithreaded apartment, and the server's objects are only ever called
/// from such threads. A read that cannot be answered comes back
/// <see cref="Answer{T}.Unanswered"/> and the caller asks winget.exe as it
/// always did. Two failures in a row turn the API off for the session: the
/// first gets a fresh connection, since the server goes away whenever App
/// Installer updates itself under a running app, and a second says the API
/// is not working on this machine.
/// </summary>
internal static partial class WingetApi
{
    /// <summary>Whether the API is still worth asking.</summary>
    public static bool IsAvailable => _available;

    /// <summary>What the last read that failed said, for whoever asks why.</summary>
    public static string? LastError => LastFailure is { } failure
        ? $"{failure.GetType().Name} 0x{failure.HResult:X8}: {failure.Message}"
        : null;

    /// <summary>The last read's failure itself, with where it happened.</summary>
    internal static Exception? LastFailure { get; private set; }

    private static volatile bool _available = true;
    private static int _failures;

    /// <summary>
    /// How long a connection to the sources is used before a new one is
    /// made. Connecting is where winget checks whether a source has a newer
    /// index, so a connection kept for ever would stop seeing new packages;
    /// five minutes is winget's own interval for that check.
    /// </summary>
    private static readonly TimeSpan SessionLife = TimeSpan.FromMinutes(5);

    private static readonly object Gate = new();
    private static Session? _session;

    /// <summary>
    /// What the sources say about one package, by exact id - the same search
    /// `winget show --id X --exact` makes, over the same sources. Null when
    /// no source has it; unanswered when the API cannot say.
    /// </summary>
    public static Task<Answer<WingetManifest?>> ShowAsync(string id, CancellationToken ct = default)
    {
        if (!_available || string.IsNullOrWhiteSpace(id))
            return Task.FromResult(Answer<WingetManifest?>.Unanswered);

        // The token stops the caller waiting; the call itself cannot be
        // interrupted and finishes on its thread.
        return Task.Run(() => Guarded(() => Show(id)), ct).WaitAsync(ct);
    }

    /// <summary>Forgets the connection, so that the next read makes a new one. For tests.</summary>
    internal static void Reset()
    {
        Forget();
        _failures = 0;
        _available = true;
        LastFailure = null;
    }

    private static Answer<T> Guarded<T>(Func<Answer<T>> read)
    {
        try
        {
            var answer = read();
            _failures = 0;
            return answer;
        }
        catch (Exception ex)
        {
            Forget();

            if (Interlocked.Increment(ref _failures) >= 2)
                _available = false;

            LastFailure = ex;
            Debug.WriteLine($"winget API: {LastError}");
            return Answer<T>.Unanswered;
        }
    }

    private static void Forget()
    {
        lock (Gate)
        {
            _session = null;
        }
    }

    // ---------------------------------------------------------------
    // Reads
    // ---------------------------------------------------------------

    private static Answer<WingetManifest?> Show(string id)
    {
        var session = Connect();
        var result = FindExact(session.Sources, id);

        // The sources could not be searched. winget.exe gets its own try,
        // which is what it would have had anyway.
        result.get_Status(out var status);
        if (status != FindOk)
            return Answer<WingetManifest?>.Unanswered;

        result.get_Matches(out var matchesPtr);
        var matches = Com.Wrap<IVectorViewOfMatchResult>(matchesPtr);
        matches.get_Size(out var count);

        // None is the plain answer. More than one - the same id in two
        // sources - is what winget refuses to show, and the page gets nothing
        // from it either way.
        if (count != 1)
            return Answer<WingetManifest?>.Of(null);

        matches.GetAt(0, out var matchPtr);
        Com.Wrap<IMatchResult>(matchPtr).get_CatalogPackage(out var packagePtr);
        var package = Com.Wrap<ICatalogPackage>(packagePtr);

        // The version winget would install, whose manifest `show` prints.
        // Without one, what winget prints is its own business.
        package.get_DefaultInstallVersion(out var versionPtr);
        if (versionPtr == IntPtr.Zero)
            return Answer<WingetManifest?>.Unanswered;

        var version = Com.Wrap<IPackageVersionInfo>(versionPtr);
        ((IPackageVersionInfo4)version).GetCatalogPackageMetadata(out var metadataPtr);
        var metadata = Com.Wrap<ICatalogPackageMetadata>(metadataPtr);

        var name = Text(metadata.get_PackageName);
        if (name.Length == 0)
            name = Text(package.get_Name);

        return Answer<WingetManifest?>.Of(new WingetManifest(
            Id: Text(package.get_Id),
            Name: name,
            Version: Text(version.get_Version),
            Publisher: Text(metadata.get_Publisher),
            PublisherUrl: Text(metadata.get_PublisherUrl),
            PublisherSupportUrl: Text(metadata.get_PublisherSupportUrl),
            Author: Text(metadata.get_Author),
            Description: Text(metadata.get_Description),
            ShortDescription: Text(metadata.get_ShortDescription),
            Homepage: Text(metadata.get_PackageUrl),
            License: Text(metadata.get_License),
            LicenseUrl: Text(metadata.get_LicenseUrl),
            PrivacyUrl: Text(metadata.get_PrivacyUrl),
            Copyright: Text(metadata.get_Copyright),
            CopyrightUrl: Text(metadata.get_CopyrightUrl),
            ReleaseNotes: Text(metadata.get_ReleaseNotes),
            ReleaseNotesUrl: Text(metadata.get_ReleaseNotesUrl),
            PurchaseUrl: Text(metadata.get_PurchaseUrl),
            InstallationNotes: Text(metadata.get_InstallationNotes),
            Tags: Strings(metadata.get_Tags),
            Documentation: Links(metadata.get_Documentations),
            Agreements: Agreements(metadata.get_Agreements)));
    }

    /// <summary>
    /// A search of the sources for one id, matched exactly - case and all -
    /// as `--exact` matches.
    /// </summary>
    private static IFindPackagesResult FindExact(IPackageCatalog sources, string id)
    {
        var optionsPtr = Create(Clsid.FindPackagesOptions, typeof(IFindPackagesOptions).GUID);
        try
        {
            var options = Share<IFindPackagesOptions>(optionsPtr);
            options.get_Selectors(out var selectorsPtr);
            var selectors = Com.Wrap<IVectorOfMatchFilter>(selectorsPtr);

            var filterPtr = Create(Clsid.PackageMatchFilter, typeof(IPackageMatchFilter).GUID);
            try
            {
                var filter = Share<IPackageMatchFilter>(filterPtr);
                filter.put_Field(MatchFieldId);
                filter.put_Option(MatchOptionEquals);

                using var value = new HString(id);
                filter.put_Value(value.Pointer);

                selectors.Append(filterPtr);
            }
            finally
            {
                Marshal.Release(filterPtr);
            }

            sources.FindPackages(optionsPtr, out var resultPtr);
            return Com.Wrap<IFindPackagesResult>(resultPtr);
        }
        finally
        {
            Marshal.Release(optionsPtr);
        }
    }

    /// <summary>
    /// Every installed package, with the newest version a source has for it
    /// and whether winget counts that as an update: the question `winget
    /// upgrade --include-unknown --include-pinned` answers, asked of the
    /// same engine without the process. Unanswered when the API cannot say.
    /// </summary>
    public static Task<Answer<IReadOnlyList<WingetInstalled>>> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        if (!_available)
            return Task.FromResult(Answer<IReadOnlyList<WingetInstalled>>.Unanswered);

        return Task.Run(() => Guarded(CheckForUpdates), ct).WaitAsync(ct);
    }

    private static Answer<IReadOnlyList<WingetInstalled>> CheckForUpdates()
    {
        var session = Connect();

        // A catalog made afresh: what is installed is read as the catalog
        // opens, and this is asked because something may have changed.
        var catalog = Compose(session.Manager, SearchLocalCatalogs);

        var optionsPtr = Create(Clsid.FindPackagesOptions, typeof(IFindPackagesOptions).GUID);
        IntPtr resultPtr;
        try
        {
            // No selector and no filter: everything the catalog has.
            catalog.FindPackages(optionsPtr, out resultPtr);
        }
        finally
        {
            Marshal.Release(optionsPtr);
        }

        var result = Com.Wrap<IFindPackagesResult>(resultPtr);
        result.get_Status(out var status);
        if (status != FindOk)
            return Answer<IReadOnlyList<WingetInstalled>>.Unanswered;

        result.get_Matches(out var matchesPtr);
        var matches = Com.Wrap<IVectorViewOfMatchResult>(matchesPtr);
        matches.get_Size(out var count);

        // Every property is a round trip to the server and a package takes
        // ten of them; eight at a time they overlap. Thread-pool threads are
        // in the apartment the proxies live in, so any of them may call.
        var rows = new WingetInstalled?[count];
        Parallel.For(0, (int)count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            Com.Initialize();
            rows[i] = ReadInstalled(matches, (uint)i, session.SourceNames);
        });

        return Answer<IReadOnlyList<WingetInstalled>>.Of(rows.Where(row => row is not null).Select(row => row!).ToList());
    }

    /// <summary>
    /// One match of the installed catalog, read the way winget's own listing
    /// reads it: the name and version of the install, the first of the
    /// versions the source lists as the newest, and winget's comparison of
    /// the two. Null for a match that is not an install after all.
    /// </summary>
    private static WingetInstalled? ReadInstalled(IVectorViewOfMatchResult matches, uint index, IReadOnlyDictionary<string, string> sourceNames)
    {
        matches.GetAt(index, out var matchPtr);
        Com.Wrap<IMatchResult>(matchPtr).get_CatalogPackage(out var packagePtr);
        var package = Com.Wrap<ICatalogPackage>(packagePtr);

        package.get_InstalledVersion(out var installedPtr);
        if (installedPtr == IntPtr.Zero)
            return null;

        var installed = Com.Wrap<IPackageVersionInfo>(installedPtr);
        var id = Text(package.get_Id);
        var name = Text(installed.get_DisplayName);
        var version = Text(installed.get_Version);

        package.get_AvailableVersions(out var keysPtr);
        var keys = Com.Wrap<IVectorViewOfVersionId>(keysPtr);
        keys.get_Size(out var keyCount);
        if (keyCount == 0)
            return new WingetInstalled(id, name, version, string.Empty, string.Empty, false);

        keys.GetAt(0, out var keyPtr);
        var key = Com.Wrap<IPackageVersionId>(keyPtr);
        var latest = Text(key.get_Version);
        var source = sourceNames.GetValueOrDefault(Text(key.get_PackageCatalogId), string.Empty);

        using var latestHandle = new HString(latest);
        ((IPackageVersionInfo2)installed).CompareToVersion(latestHandle.Pointer, out var comparison);

        return new WingetInstalled(id, name, version, latest, source, comparison == CompareLesser);
    }

    /// <summary>
    /// How many icon lookups run at once. Each holds a thread-pool thread
    /// for its call into the server, and a page of cards or a Manage page of
    /// installed rows asks about every package on it in one go.
    /// </summary>
    private static readonly SemaphoreSlim IconGate = new(4);

    /// <summary>
    /// The icons a package's manifest declares, by exact id, from the sources
    /// whose index is on this machine - a lookup that costs nothing where the
    /// Store's web service would be a request for every package on a page,
    /// and the Store's own logo is reached another way. Empty when the
    /// manifest declares none, or no indexed source has the package.
    /// </summary>
    public static async Task<Answer<IReadOnlyList<WingetIcon>>> IconsAsync(string id, CancellationToken ct = default)
    {
        if (!_available || string.IsNullOrWhiteSpace(id))
            return Answer<IReadOnlyList<WingetIcon>>.Unanswered;

        await IconGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Guarded(() => Icons(id)), ct).WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            IconGate.Release();
        }
    }

    private static Answer<IReadOnlyList<WingetIcon>> Icons(string id)
    {
        var session = Connect();
        var result = FindExact(session.Indexed, id);

        result.get_Status(out var status);
        if (status != FindOk)
            return Answer<IReadOnlyList<WingetIcon>>.Unanswered;

        result.get_Matches(out var matchesPtr);
        var matches = Com.Wrap<IVectorViewOfMatchResult>(matchesPtr);
        matches.get_Size(out var count);
        if (count == 0)
            return Answer<IReadOnlyList<WingetIcon>>.Of([]);

        matches.GetAt(0, out var matchPtr);
        Com.Wrap<IMatchResult>(matchPtr).get_CatalogPackage(out var packagePtr);
        Com.Wrap<ICatalogPackage>(packagePtr).get_DefaultInstallVersion(out var versionPtr);
        if (versionPtr == IntPtr.Zero)
            return Answer<IReadOnlyList<WingetIcon>>.Of([]);

        ((IPackageVersionInfo4)Com.Wrap<IPackageVersionInfo>(versionPtr)).GetCatalogPackageMetadata(out var metadataPtr);
        var metadata = Com.Wrap<ICatalogPackageMetadata>(metadataPtr);

        metadata.get_Icons(out var iconsPtr);
        var vector = Com.Wrap<IVectorViewOfIcon>(iconsPtr);
        vector.get_Size(out var iconCount);

        var icons = new List<WingetIcon>((int)iconCount);
        for (uint i = 0; i < iconCount; i++)
        {
            vector.GetAt(i, out var iconPtr);
            var icon = Com.Wrap<IIcon>(iconPtr);
            icon.get_FileType(out var type);
            icon.get_Resolution(out var resolution);
            icon.get_Theme(out var theme);

            icons.Add(new WingetIcon(
                Text(icon.get_Url),
                Enum.IsDefined((WingetIconType)type) ? (WingetIconType)type : WingetIconType.Unknown,
                resolution >= 0 && resolution < IconPixels.Length ? IconPixels[resolution] : 0,
                Enum.IsDefined((WingetIconTheme)theme) ? (WingetIconTheme)theme : WingetIconTheme.Unknown));
        }

        return Answer<IReadOnlyList<WingetIcon>>.Of(icons);
    }

    /// <summary>The side in pixels behind each value of the IDL's IconResolution, Custom first.</summary>
    private static readonly int[] IconPixels = [0, 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256];

    // ---------------------------------------------------------------
    // Connection
    // ---------------------------------------------------------------

    private sealed class Session(
        IPackageManager manager,
        IPackageCatalog sources,
        IPackageCatalog indexed,
        IReadOnlyDictionary<string, string> sourceNames,
        long connectedAt)
    {
        public IPackageManager Manager { get; } = manager;

        /// <summary>Every source winget would search when none is named, as one catalog of what they know.</summary>
        public IPackageCatalog Sources { get; } = sources;

        /// <summary>
        /// The sources among those whose index is a file on this machine,
        /// where a lookup costs nothing: for the questions asked of many
        /// packages at once, which the Store's web service need not hear.
        /// </summary>
        public IPackageCatalog Indexed { get; } = indexed;

        /// <summary>The name of each of those sources - "winget", "msstore" - by the identifier a version key carries.</summary>
        public IReadOnlyDictionary<string, string> SourceNames { get; } = sourceNames;

        public bool IsFresh => Environment.TickCount64 - connectedAt < SessionLife.TotalMilliseconds;
    }

    private static Session Connect()
    {
        lock (Gate)
        {
            if (_session is { IsFresh: true } current)
                return current;
        }

        Com.Initialize();

        var manager = Com.Wrap<IPackageManager>(Create(Clsid.PackageManager, typeof(IPackageManager).GUID));
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sources = Compose(manager, SearchRemoteCatalogs, names);
        var indexed = Compose(manager, SearchRemoteCatalogs, indexedOnly: true);
        var session = new Session(manager, sources, indexed, names, Environment.TickCount64);

        lock (Gate)
        {
            _session = session;
        }

        return session;
    }

    /// <summary>
    /// One catalog over the sources winget searches when none is named: the
    /// configured ones, less any marked explicit, which winget leaves out
    /// unless it is asked for by name. Composed for one of two readings:
    /// remote packages only, for what the sources know of a package, or the
    /// installed packages with what the sources know of each, for what is
    /// on the machine and what it could become.
    ///
    /// The sources are opened without winget's check for a newer index. The
    /// two winget.exe reads of every refresh make that check as they always
    /// did, and leaving it to them keeps this server from ever replacing the
    /// index under a winget process - the one contention that is known to
    /// make a read fail.
    /// </summary>
    private static IPackageCatalog Compose(
        IPackageManager manager,
        int behaviour,
        Dictionary<string, string>? names = null,
        bool indexedOnly = false)
    {
        var optionsPtr = Create(Clsid.CreateCompositePackageCatalogOptions, typeof(ICreateCompositePackageCatalogOptions).GUID);
        try
        {
            var options = Share<ICreateCompositePackageCatalogOptions>(optionsPtr);
            options.get_Catalogs(out var membersPtr);
            var members = Com.Wrap<IVectorOfCatalogReference>(membersPtr);

            manager.GetPackageCatalogs(out var knownPtr);
            var known = Com.Wrap<IVectorViewOfCatalogReference>(knownPtr);
            known.get_Size(out var count);

            for (uint i = 0; i < count; i++)
            {
                known.GetAt(i, out var referencePtr);
                try
                {
                    var reference = Share<IPackageCatalogReference>(referencePtr);

                    reference.get_Info(out var infoPtr);
                    var info = Com.Wrap<IPackageCatalogInfo>(infoPtr);
                    ((IPackageCatalogInfo2)info).get_Explicit(out var isExplicit);
                    if (isExplicit != 0)
                        continue;

                    // A pre-indexed source is a database file winget keeps on
                    // the machine; the other kind is a web service, asked
                    // over the network for every search.
                    if (indexedOnly && !string.Equals(Text(info.get_Type), "Microsoft.PreIndexed.Package", StringComparison.OrdinalIgnoreCase))
                        continue;

                    names?.TryAdd(Text(info.get_Id), Text(info.get_Name));

                    // What --accept-source-agreements says on the command line.
                    ((IPackageCatalogReference3)reference).put_AcceptSourceAgreements(1);
                    ((IPackageCatalogReference4)reference).put_PackageCatalogBackgroundUpdateInterval(0);
                    members.Append(referencePtr);
                }
                finally
                {
                    Marshal.Release(referencePtr);
                }
            }

            options.put_CompositeSearchBehavior(behaviour);

            manager.CreateCompositePackageCatalog(optionsPtr, out var compositePtr);
            Com.Wrap<IPackageCatalogReference>(compositePtr).Connect(out var resultPtr);

            var result = Com.Wrap<IConnectResult>(resultPtr);
            result.get_Status(out var status);
            if (status != ConnectOk)
                throw new InvalidOperationException($"The sources could not be opened (status {status}).");

            result.get_PackageCatalog(out var catalogPtr);
            return Com.Wrap<IPackageCatalog>(catalogPtr);
        }
        finally
        {
            Marshal.Release(optionsPtr);
        }
    }

    // ---------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------

    private static IntPtr Create(Guid clsid, Guid iid) =>
        Com.Create(clsid, iid, allowLowerTrust: Environment.IsPrivilegedProcess);

    /// <summary>
    /// A wrapper over a pointer the caller goes on holding - to pass on to
    /// the server, which wants the pointer, not the wrapper. The wrapper
    /// takes a reference of its own.
    /// </summary>
    private static T Share<T>(IntPtr pointer) where T : class
    {
        Marshal.AddRef(pointer);
        return Com.Wrap<T>(pointer);
    }

    private delegate void Getter(out IntPtr value);

    private static string Text(Getter get)
    {
        get(out var handle);
        return HString.Read(handle);
    }

    private static List<string> Strings(Getter get)
    {
        get(out var vectorPtr);
        var vector = Com.Wrap<IVectorViewOfString>(vectorPtr);
        vector.get_Size(out var count);

        var items = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            vector.GetAt(i, out var handle);
            items.Add(HString.Read(handle));
        }

        return items;
    }

    private static List<WingetLink> Links(Getter get)
    {
        get(out var vectorPtr);
        var vector = Com.Wrap<IVectorViewOfDocumentation>(vectorPtr);
        vector.get_Size(out var count);

        var items = new List<WingetLink>((int)count);
        for (uint i = 0; i < count; i++)
        {
            vector.GetAt(i, out var itemPtr);
            var item = Com.Wrap<IDocumentation>(itemPtr);
            items.Add(new WingetLink(Text(item.get_DocumentLabel), Text(item.get_DocumentUrl)));
        }

        return items;
    }

    private static List<WingetAgreement> Agreements(Getter get)
    {
        get(out var vectorPtr);
        var vector = Com.Wrap<IVectorViewOfAgreement>(vectorPtr);
        vector.get_Size(out var count);

        var items = new List<WingetAgreement>((int)count);
        for (uint i = 0; i < count; i++)
        {
            vector.GetAt(i, out var itemPtr);
            var item = Com.Wrap<IPackageAgreement>(itemPtr);
            items.Add(new WingetAgreement(Text(item.get_Label), Text(item.get_Text), Text(item.get_Url)));
        }

        return items;
    }

    /// <summary>
    /// The production out-of-process class ids, from winget-cli's
    /// src/Microsoft.Management.Deployment/Public/ComClsids.h. The in-process
    /// and development ids there are not these.
    /// </summary>
    private static class Clsid
    {
        public static readonly Guid PackageManager = new("C53A4F16-787E-42A4-B304-29EFFB4BF597");
        public static readonly Guid CreateCompositePackageCatalogOptions = new("526534B8-7E46-47C8-8416-B1685C327D37");
        public static readonly Guid FindPackagesOptions = new("572DED96-9C60-4526-8F92-EE7D91D38C1A");
        public static readonly Guid PackageMatchFilter = new("D02C9DAF-99DC-429C-B503-4E504E4AB000");
    }

    // Enumeration values, from PackageManager.idl.
    private const int ConnectOk = 0;
    private const int FindOk = 0;
    private const int MatchFieldId = 1;
    private const int MatchOptionEquals = 0;
    private const int SearchLocalCatalogs = 0; // CompositeSearchBehavior.LocalCatalogs: the installed packages, correlated with the sources
    private const int SearchRemoteCatalogs = 1; // CompositeSearchBehavior.RemotePackagesFromRemoteCatalogs
    private const int CompareLesser = 1; // CompareResult.Lesser: this version is older than the one given

    // ---------------------------------------------------------------
    // Interfaces, in vtable order from Microsoft.Management.Deployment.winmd
    // (App Installer 1.29.380). Each contract version of a class is an
    // interface of its own, numbered in the order the IDL adds them; the
    // rules for the declarations are on WinRt.IInspectable.
    // ---------------------------------------------------------------

    [GeneratedComInterface]
    [Guid("B375E3B9-F2E0-5C93-87A7-B67497F7E593")]
    internal partial interface IPackageManager : IInspectable
    {
        void GetPackageCatalogs(out IntPtr references);
        void GetPredefinedPackageCatalog();
        void GetLocalPackageCatalog();
        void GetPackageCatalogByName();
        void CreateCompositePackageCatalog(IntPtr options, out IntPtr reference);
    }

    [GeneratedComInterface]
    [Guid("84354608-3C95-55F0-8836-78ADA3D2C203")]
    internal partial interface IPackageCatalogReference : IInspectable
    {
        void get_IsComposite();
        void get_Info(out IntPtr info);
        void ConnectAsync();
        void Connect(out IntPtr result);
    }

    /// <summary>The third contract of PackageCatalogReference: the source agreements.</summary>
    [GeneratedComInterface]
    [Guid("10FD6007-DA3D-529C-A917-9138AAE34C2E")]
    internal partial interface IPackageCatalogReference3 : IInspectable
    {
        void get_SourceAgreements();
        void get_AcceptSourceAgreements();
        void put_AcceptSourceAgreements(byte value);
    }

    /// <summary>
    /// The fourth contract of PackageCatalogReference: how often the source
    /// is checked for a newer index. A WinRT TimeSpan is one 64-bit count of
    /// 100-nanosecond ticks, passed as such; zero turns the check off.
    /// </summary>
    [GeneratedComInterface]
    [Guid("12C6B72D-F7F6-501E-9CD2-DCD8B641A3AA")]
    internal partial interface IPackageCatalogReference4 : IInspectable
    {
        void get_PackageCatalogBackgroundUpdateInterval();
        void put_PackageCatalogBackgroundUpdateInterval(long ticks);
    }

    [GeneratedComInterface]
    [Guid("FAAAA46D-78F2-5567-B228-A7A6385EAED8")]
    internal partial interface IPackageCatalogInfo : IInspectable
    {
        void get_Id(out IntPtr id);
        void get_Name(out IntPtr name);
        void get_Type(out IntPtr type);
    }

    /// <summary>The second contract of PackageCatalogInfo: whether the source is searched only by name.</summary>
    [GeneratedComInterface]
    [Guid("AE6FB7DB-40FD-52EC-BD01-86A06681AFC3")]
    internal partial interface IPackageCatalogInfo2 : IInspectable
    {
        void get_Explicit(out byte isExplicit);
    }

    [GeneratedComInterface]
    [Guid("7E9BD693-9A94-5EC9-8EAF-70A8156B873D")]
    internal partial interface IConnectResult : IInspectable
    {
        void get_Status(out int status);
        void get_PackageCatalog(out IntPtr catalog);
    }

    [GeneratedComInterface]
    [Guid("036A0492-9532-54E2-84A0-B4418E4403B4")]
    internal partial interface IPackageCatalog : IInspectable
    {
        void get_IsComposite();
        void get_Info();
        void FindPackagesAsync();
        void FindPackages(IntPtr options, out IntPtr result);
    }

    [GeneratedComInterface]
    [Guid("21ABAA76-089D-51C5-A745-C85EEFE70116")]
    internal partial interface ICreateCompositePackageCatalogOptions : IInspectable
    {
        void get_Catalogs(out IntPtr catalogs);
        void get_CompositeSearchBehavior();
        void put_CompositeSearchBehavior(int behavior);
    }

    [GeneratedComInterface]
    [Guid("A5270EDD-7DA7-57A3-BACE-F2593553561F")]
    internal partial interface IFindPackagesOptions : IInspectable
    {
        void get_Selectors(out IntPtr selectors);
        void get_Filters(out IntPtr filters);
        void get_ResultLimit();
        void put_ResultLimit(uint limit);
    }

    [GeneratedComInterface]
    [Guid("D981ECA3-4DE5-5AD7-967A-698C7D60FC3B")]
    internal partial interface IPackageMatchFilter : IInspectable
    {
        void get_Option();
        void put_Option(int option);
        void get_Field();
        void put_Field(int field);
        void get_Value();
        void put_Value(IntPtr value);
    }

    [GeneratedComInterface]
    [Guid("8760DC7A-A46B-5511-9986-826F8B0A0941")]
    internal partial interface IFindPackagesResult : IInspectable
    {
        void get_Status(out int status);
        void get_Matches(out IntPtr matches);
        void get_WasLimitExceeded(out byte exceeded);
    }

    [GeneratedComInterface]
    [Guid("5500B7AE-25EB-5CD4-8FBB-9D1CB23B5696")]
    internal partial interface IMatchResult : IInspectable
    {
        void get_CatalogPackage(out IntPtr package);
    }

    [GeneratedComInterface]
    [Guid("7ACEAB52-4735-53D5-8FD1-8CF7F64838DC")]
    internal partial interface ICatalogPackage : IInspectable
    {
        void get_Id(out IntPtr id);
        void get_Name(out IntPtr name);
        void get_InstalledVersion(out IntPtr version);
        void get_AvailableVersions(out IntPtr versions);
        void get_DefaultInstallVersion(out IntPtr version);
        void GetPackageVersionInfo();
        void get_IsUpdateAvailable(out byte available);
    }

    [GeneratedComInterface]
    [Guid("13202AE0-8F11-58DD-A91C-917DF75C6DC0")]
    internal partial interface IPackageVersionInfo : IInspectable
    {
        void GetMetadata(int field, out IntPtr value);
        void get_Id(out IntPtr id);
        void get_DisplayName(out IntPtr name);
        void get_Version(out IntPtr version);
        void get_Channel();
        void get_PackageFamilyNames();
        void get_ProductCodes();
        void get_PackageCatalog(out IntPtr catalog);
    }

    /// <summary>The second contract of PackageVersionInfo: winget's own ordering of versions.</summary>
    [GeneratedComInterface]
    [Guid("69BE5693-63B7-52D5-A66D-C3FC132C1C9B")]
    internal partial interface IPackageVersionInfo2 : IInspectable
    {
        void CompareToVersion(IntPtr version, out int result);
    }

    [GeneratedComInterface]
    [Guid("75A4B385-EE41-5607-A6C3-EB4DCB348093")]
    internal partial interface IPackageVersionId : IInspectable
    {
        void get_PackageCatalogId(out IntPtr id);
        void get_Version(out IntPtr version);
        void get_Channel(out IntPtr channel);
    }

    /// <summary>The fourth contract of PackageVersionInfo: the manifest behind the version.</summary>
    [GeneratedComInterface]
    [Guid("80A48D20-594B-576A-B40E-4188EA373198")]
    internal partial interface IPackageVersionInfo4 : IInspectable
    {
        void GetCatalogPackageMetadata(out IntPtr metadata);
        void GetCatalogPackageMetadataForLocale();
        void GetApplicableInstaller(IntPtr options, out IntPtr installer);
    }

    [GeneratedComInterface]
    [Guid("FFD4E24F-5ADD-5728-A9C0-D8803622A753")]
    internal partial interface ICatalogPackageMetadata : IInspectable
    {
        void get_Locale(out IntPtr value);
        void get_Publisher(out IntPtr value);
        void get_PublisherUrl(out IntPtr value);
        void get_PublisherSupportUrl(out IntPtr value);
        void get_PrivacyUrl(out IntPtr value);
        void get_Author(out IntPtr value);
        void get_PackageName(out IntPtr value);
        void get_PackageUrl(out IntPtr value);
        void get_License(out IntPtr value);
        void get_LicenseUrl(out IntPtr value);
        void get_Copyright(out IntPtr value);
        void get_CopyrightUrl(out IntPtr value);
        void get_ShortDescription(out IntPtr value);
        void get_Description(out IntPtr value);
        void get_Tags(out IntPtr strings);
        void get_Agreements(out IntPtr agreements);
        void get_Documentations(out IntPtr documentations);
        void get_Icons(out IntPtr icons);
        void get_ReleaseNotes(out IntPtr value);
        void get_ReleaseNotesUrl(out IntPtr value);
        void get_PurchaseUrl(out IntPtr value);
        void get_InstallationNotes(out IntPtr value);
    }

    [GeneratedComInterface]
    [Guid("2E8D35DD-7677-583C-836D-FF52EBBFF39A")]
    internal partial interface IDocumentation : IInspectable
    {
        void get_DocumentLabel(out IntPtr label);
        void get_DocumentUrl(out IntPtr url);
    }

    [GeneratedComInterface]
    [Guid("077C0159-5516-5920-9E8E-F15704224C42")]
    internal partial interface IPackageAgreement : IInspectable
    {
        void get_Label(out IntPtr label);
        void get_Text(out IntPtr text);
        void get_Url(out IntPtr url);
    }

    /// <summary>IVectorView&lt;PackageCatalogReference&gt;.</summary>
    [GeneratedComInterface]
    [Guid("B82BD1FA-E49A-5AD4-96F9-827B46E735A4")]
    internal partial interface IVectorViewOfCatalogReference : IInspectable
    {
        void GetAt(uint index, out IntPtr item);
        void get_Size(out uint size);
    }

    /// <summary>IVector&lt;PackageCatalogReference&gt;.</summary>
    [GeneratedComInterface]
    [Guid("40748917-9F09-540A-B286-F3E291F19D5C")]
    internal partial interface IVectorOfCatalogReference : IInspectable
    {
        void GetAt();
        void get_Size();
        void GetView();
        void IndexOf();
        void SetAt();
        void InsertAt();
        void RemoveAt();
        void Append(IntPtr item);
    }

    /// <summary>IVector&lt;PackageMatchFilter&gt;.</summary>
    [GeneratedComInterface]
    [Guid("C7F2899B-D2B8-5EEA-A4F2-B637D1B2B792")]
    internal partial interface IVectorOfMatchFilter : IInspectable
    {
        void GetAt();
        void get_Size();
        void GetView();
        void IndexOf();
        void SetAt();
        void InsertAt();
        void RemoveAt();
        void Append(IntPtr item);
    }

    /// <summary>IVectorView&lt;MatchResult&gt;.</summary>
    [GeneratedComInterface]
    [Guid("1D883FEA-7C0B-5F4B-B3FA-0CFD50D09FAB")]
    internal partial interface IVectorViewOfMatchResult : IInspectable
    {
        void GetAt(uint index, out IntPtr item);
        void get_Size(out uint size);
    }

    /// <summary>IVectorView&lt;String&gt;.</summary>
    [GeneratedComInterface]
    [Guid("2F13C006-A03A-5F69-B090-75A43E33423E")]
    internal partial interface IVectorViewOfString : IInspectable
    {
        void GetAt(uint index, out IntPtr item);
        void get_Size(out uint size);
    }

    /// <summary>IVectorView&lt;Documentation&gt;.</summary>
    [GeneratedComInterface]
    [Guid("99E04C3C-19FE-540B-894F-AA33A16B3C72")]
    internal partial interface IVectorViewOfDocumentation : IInspectable
    {
        void GetAt(uint index, out IntPtr item);
        void get_Size(out uint size);
    }

    /// <summary>IVectorView&lt;PackageAgreement&gt;.</summary>
    [GeneratedComInterface]
    [Guid("83ED4BDF-C17C-543B-9EEB-58063C31D0C5")]
    internal partial interface IVectorViewOfAgreement : IInspectable
    {
        void GetAt(uint index, out IntPtr item);
        void get_Size(out uint size);
    }

    /// <summary>IVectorView&lt;PackageVersionId&gt;.</summary>
    [GeneratedComInterface]
    [Guid("80E3FFEC-CB53-52D8-9BC7-005BA4634695")]
    internal partial interface IVectorViewOfVersionId : IInspectable
    {
        void GetAt(uint index, out IntPtr item);
        void get_Size(out uint size);
    }

    [GeneratedComInterface]
    [Guid("90D54C45-4F49-5CE7-8337-1F150D853A32")]
    internal partial interface IIcon : IInspectable
    {
        void get_Url(out IntPtr url);
        void get_FileType(out int type);
        void get_Resolution(out int resolution);
        void get_Theme(out int theme);
    }

    /// <summary>IVectorView&lt;Icon&gt;.</summary>
    [GeneratedComInterface]
    [Guid("CD4CCBDF-4A82-50F6-BFBB-AA0B8DD8BECA")]
    internal partial interface IVectorViewOfIcon : IInspectable
    {
        void GetAt(uint index, out IntPtr item);
        void get_Size(out uint size);
    }
}

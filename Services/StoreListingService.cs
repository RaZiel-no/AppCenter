using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using AppCenter.Models;

namespace AppCenter.Services;

/// <summary>
/// One image from a Store listing. <see cref="Purpose"/> is the Store's own
/// tag: "Screenshot", "Logo", "Tile", "Poster", "BoxArt", "Hero".
/// </summary>
public sealed record StoreImage(string Purpose, int Width, int Height, string Url);

/// <summary>What the Store says about one product.</summary>
public sealed class StoreListing
{
    public required string StoreId { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<StoreImage> Images { get; init; }

    /// <summary>The screenshots, largest first.</summary>
    public IReadOnlyList<string> Screenshots =>
        Images
            .Where(image => image.Purpose == "Screenshot")
            .OrderByDescending(image => image.Width * image.Height)
            .Select(image => image.Url)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The square image that stands for the app. Anything tagged as a logo
    /// wins at whatever size it comes in - the Store's 50/75/100px logos are
    /// the app's icon at 100/150/200%, and a small true icon beats a large
    /// padded one. Only a listing with no logo at all falls back to a square
    /// tile, which is the icon drawn small in the middle of a safe zone: it
    /// looks like the app, but at a third of the size.
    /// </summary>
    public string? Logo =>
        Images
            .Where(image => image.Width == image.Height)
            .Where(image => image.Purpose == "Logo" || (image.Purpose == "Tile" && image.Width >= 150))
            .OrderBy(image => image.Purpose == "Logo" ? 0 : 1)
            .ThenByDescending(image => image.Width)
            .Select(image => image.Url)
            .FirstOrDefault();
}

/// <summary>
/// Asks the Microsoft Store what it knows about a product. A packaged
/// listing is read through the documented <c>Windows.Services.Store</c> API:
/// the same call an app makes to read its own listing, given another app's
/// id. Nothing is signed in and nothing is sent that the Store client on this
/// machine does not send anyway. The Win32 apps the Store also carries are
/// not that API's to answer for; they come from the Store's web catalogue
/// instead, in the other half of this class.
///
/// Reached by hand-written COM interop rather than the WinRT projection,
/// because the projection is a Windows SDK target framework and a
/// twenty-megabyte assembly for two method calls. The interfaces below are
/// declared in vtable order straight from the SDK headers; the parameterised
/// ones (IAsyncOperation, IMapView and so on) carry the IIDs the WinRT
/// signature hash produces for these type arguments.
///
/// Every lookup runs to completion on one thread-pool thread, polling the
/// operation rather than registering a completion handler: one fewer COM
/// object to provide, and a listing takes half a second, not minutes.
/// </summary>
public static partial class StoreListings
{
    /// <summary>
    /// Whether the Store is worth asking at all. Set false the first time the
    /// activation factory is missing - an LTSC or Server machine with no Store
    /// - so that a page of cards does not fail one by one.
    /// </summary>
    private static volatile bool _available = true;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How many lookups run at once. Each one holds a thread-pool thread for
    /// the half second the Store takes to answer, and a Manage page showing
    /// system packages asks about sixty MSIX rows in one go: unbounded, that
    /// is sixty blocked threads and a pool that grows one at a time to meet
    /// them, with everything else the app hands the pool waiting behind.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(4);

    /// <summary>The listing for a Store product id such as <c>9N0DX20HK701</c>.</summary>
    public static Task<StoreListing?> GetAsync(string storeId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(storeId))
            return Task.FromResult<StoreListing?>(null);

        storeId = storeId.Trim();

        // A Win32 listing is not the Store client's to answer, and needs no
        // Store on the machine to be asked about.
        if (!IsPackagedId(storeId))
            return LookupOnWebAsync(storeId).WaitAsync(ct);

        if (!_available)
            return Task.FromResult<StoreListing?>(null);

        return Gated(() => LookupById(storeId, ct), ct);
    }

    /// <summary>
    /// The listing for an installed MSIX package, found by its family name
    /// (<c>Microsoft.WindowsTerminal_8wekyb3d8bbwe</c>). Exact where a name
    /// search is not: the Store is asked about this very package.
    /// </summary>
    public static Task<StoreListing?> GetForPackageFamilyAsync(string familyName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(familyName) || !_available)
            return Task.FromResult<StoreListing?>(null);

        return Gated(() => LookupByFamily(familyName.Trim(), ct), ct);
    }

    private static async Task<StoreListing?> Gated(Func<StoreListing?> lookup, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Guarded(lookup), ct).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// The listing behind a package, by whichever of its identities the Store
    /// can be asked about: a Store id first, else the family name of an
    /// installed MSIX package. Null when it has neither, or the Store has no
    /// listing for it.
    ///
    /// Remembered for the session per package: the detail page wants the
    /// screenshots and the icon hunt wants the logo, and that is one listing,
    /// not two round trips. The shared lookup runs without the caller's token
    /// - one page navigating away must not cancel it for the next - and each
    /// caller waits on it with its own.
    /// </summary>
    public static Task<StoreListing?> ForPackageAsync(AppPackage package, CancellationToken ct = default)
    {
        var storeId = package.StoreId;
        if (!string.IsNullOrWhiteSpace(storeId))
            return Memo.GetOrAdd("id:" + storeId, _ => GetAsync(storeId)).WaitAsync(ct);

        var family = package.PackageFamilyName;
        if (!string.IsNullOrWhiteSpace(family))
            return Memo.GetOrAdd("pfn:" + family, _ => GetForPackageFamilyAsync(family)).WaitAsync(ct);

        return Task.FromResult<StoreListing?>(null);
    }

    /// <summary>True when <see cref="ForPackageAsync"/> has something to ask about.</summary>
    public static bool CanAskAbout(AppPackage package) =>
        !string.IsNullOrWhiteSpace(package.StoreId) || !string.IsNullOrWhiteSpace(package.PackageFamilyName);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<StoreListing?>> Memo =
        new(StringComparer.OrdinalIgnoreCase);

    private static StoreListing? Guarded(Func<StoreListing?> lookup)
    {
        try
        {
            return lookup();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            // A listing is decoration. Whatever went wrong - the Store
            // service not running, a product the region does not carry, an
            // interface this Windows does not have - the answer is "nothing".
            return null;
        }
    }

    // ---------------------------------------------------------------
    // Lookups
    // ---------------------------------------------------------------

    private static StoreListing? LookupById(string storeId, CancellationToken ct)
    {
        var context = StoreContext();
        if (context is null)
            return null;

        using var kinds = new HStringList("Application");
        using var ids = new HStringList(storeId);

        context.GetStoreProductsAsync(kinds.Pointer, ids.Pointer, out var operation);

        var result = Await<IStoreProductQueryResultOperation>(operation, ct, op => { op.GetResults(out var r); return r; });
        if (result == IntPtr.Zero)
            return null;

        var query = Com.Wrap<IStoreProductQueryResult>(result);
        query.get_ExtendedError(out var error);
        if (error < 0)
            return null;

        query.get_Products(out var productsPtr);
        var products = Com.Wrap<IMapViewOfStoreProduct>(productsPtr);

        using var key = new HString(storeId);
        products.HasKey(key.Pointer, out var found);
        if (found == 0)
            return null;

        products.Lookup(key.Pointer, out var productPtr);
        return ReadProduct(Com.Wrap<IStoreProduct>(productPtr));
    }

    private static StoreListing? LookupByFamily(string familyName, CancellationToken ct)
    {
        var package = InstalledPackage(familyName);
        if (package == IntPtr.Zero)
            return null;

        try
        {
            var context = StoreContext();
            if (context is null)
                return null;

            // FindStoreProductForPackageAsync arrived on the second version
            // of the interface; the wrapper answers the cast with the right QI.
            var context2 = (IStoreContext2)context;

            using var kinds = new HStringList("Application");
            context2.FindStoreProductForPackageAsync(kinds.Pointer, package, out var operation);

            var result = Await<IStoreProductResultOperation>(operation, ct, op => { op.GetResults(out var r); return r; });
            if (result == IntPtr.Zero)
                return null;

            var wrapped = Com.Wrap<IStoreProductResult>(result);
            wrapped.get_ExtendedError(out var error);
            if (error < 0)
                return null;

            wrapped.get_Product(out var productPtr);
            if (productPtr == IntPtr.Zero)
                return null;

            return ReadProduct(Com.Wrap<IStoreProduct>(productPtr));
        }
        finally
        {
            Marshal.Release(package);
        }
    }

    private static StoreListing ReadProduct(IStoreProduct product)
    {
        product.get_StoreId(out var idHandle);
        product.get_Title(out var titleHandle);
        product.get_Images(out var imagesPtr);

        var images = new List<StoreImage>();
        var vector = Com.Wrap<IVectorViewOfStoreImage>(imagesPtr);
        vector.get_Size(out var count);

        for (uint i = 0; i < count; i++)
        {
            vector.GetAt(i, out var imagePtr);
            var image = Com.Wrap<IStoreImage>(imagePtr);

            image.get_Uri(out var uriPtr);
            if (uriPtr == IntPtr.Zero)
                continue;

            Com.Wrap<IUriRuntimeClass>(uriPtr).get_AbsoluteUri(out var urlHandle);
            image.get_ImagePurposeTag(out var purposeHandle);
            image.get_Width(out var width);
            image.get_Height(out var height);

            var url = HString.Read(urlHandle);
            if (url.Length == 0)
                continue;

            // The Store hands these out as http://; the CDN answers https://
            // just the same, and there is no reason to fetch a picture in clear.
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                url = string.Concat("https://", url.AsSpan(7));

            images.Add(new StoreImage(HString.Read(purposeHandle), (int)width, (int)height, url));
        }

        return new StoreListing
        {
            StoreId = HString.Read(idHandle),
            Title = HString.Read(titleHandle),
            Images = images,
        };
    }

    // ---------------------------------------------------------------
    // Activation
    // ---------------------------------------------------------------

    private static IStoreContext? StoreContext()
    {
        Com.Initialize();

        using var className = new HString("Windows.Services.Store.StoreContext");
        var iid = typeof(IStoreContextStatics).GUID;

        var hr = Native.RoGetActivationFactory(className.Pointer, in iid, out var factoryPtr);
        if (hr < 0)
        {
            // REGDB_E_CLASSNOTREG: there is no Store on this Windows. Any
            // other failure is left to be tried again.
            if (hr == unchecked((int)0x80040154))
                _available = false;

            return null;
        }

        var factory = Com.Wrap<IStoreContextStatics>(factoryPtr);
        factory.GetDefault(out var contextPtr);
        return Com.Wrap<IStoreContext>(contextPtr);
    }

    /// <summary>
    /// The installed package with this family name, for the current user, as
    /// a raw IPackage pointer the caller releases. Zero when none is installed.
    /// </summary>
    private static IntPtr InstalledPackage(string familyName)
    {
        Com.Initialize();

        using var className = new HString("Windows.Management.Deployment.PackageManager");
        var hr = Native.RoActivateInstance(className.Pointer, out var managerPtr);
        if (hr < 0)
            return IntPtr.Zero;

        var manager = Com.Wrap<IPackageManager>(managerPtr);

        // An empty security id means the calling user.
        using var user = new HString(string.Empty);
        using var family = new HString(familyName);
        manager.FindPackagesByUserSecurityIdPackageFamilyName(user.Pointer, family.Pointer, out var iterablePtr);

        var iterable = Com.Wrap<IIterableOfPackage>(iterablePtr);
        iterable.First(out var iteratorPtr);
        var iterator = Com.Wrap<IIteratorOfPackage>(iteratorPtr);

        iterator.get_HasCurrent(out var has);
        if (has == 0)
            return IntPtr.Zero;

        iterator.get_Current(out var packagePtr);
        return packagePtr;
    }

    // ---------------------------------------------------------------
    // Async
    // ---------------------------------------------------------------

    private enum AsyncStatus
    {
        Started = 0,
        Completed = 1,
        Canceled = 2,
        Error = 3,
    }

    /// <summary>
    /// Waits for a WinRT operation by polling its status, then reads the
    /// result out of it. The operation is closed either way.
    /// </summary>
    private static IntPtr Await<TOperation>(IntPtr operationPtr, CancellationToken ct, Func<TOperation, IntPtr> results)
        where TOperation : class
    {
        var operation = Com.Wrap<TOperation>(operationPtr);
        var info = (IAsyncInfo)operation;

        try
        {
            var deadline = Environment.TickCount64 + (long)Timeout.TotalMilliseconds;

            while (true)
            {
                info.get_Status(out var status);

                if ((AsyncStatus)status == AsyncStatus.Completed)
                    return results(operation);

                if ((AsyncStatus)status != AsyncStatus.Started)
                    return IntPtr.Zero;

                if (ct.IsCancellationRequested || Environment.TickCount64 > deadline)
                {
                    info.Cancel();
                    return IntPtr.Zero;
                }

                Thread.Sleep(20);
            }
        }
        finally
        {
            info.Close();
        }
    }

    // ---------------------------------------------------------------
    // COM plumbing
    // ---------------------------------------------------------------

    private static class Com
    {
        private static readonly StrategyBasedComWrappers Wrappers = new();

        /// <summary>
        /// Takes ownership of a raw interface pointer and hands back a wrapper
        /// that can be cast to any of the interfaces declared below. The
        /// wrapper holds its own references, so the caller's one is released
        /// here.
        /// </summary>
        public static T Wrap<T>(IntPtr pointer) where T : class
        {
            if (pointer == IntPtr.Zero)
                throw new InvalidOperationException("The Store returned no object where one was expected.");

            try
            {
                return (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }

        /// <summary>
        /// The reverse: an interface pointer for one of our own objects, as the
        /// interface the callee expects rather than the IUnknown the wrapper
        /// gives out. Released by the caller.
        /// </summary>
        public static IntPtr Expose(object instance, Guid iid)
        {
            var unknown = Wrappers.GetOrCreateComInterfaceForObject(instance, CreateComInterfaceFlags.None);
            try
            {
                var hr = Marshal.QueryInterface(unknown, in iid, out var pointer);
                if (hr < 0)
                    Marshal.ThrowExceptionForHR(hr);

                return pointer;
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        /// <summary>
        /// Prepares the calling thread for WinRT. Thread-pool threads are
        /// already in the multithreaded apartment, so this mostly says S_FALSE;
        /// a thread initialised the other way says RPC_E_CHANGED_MODE, and
        /// either is fine to go on from.
        /// </summary>
        public static void Initialize()
        {
            var hr = Native.RoInitialize(1 /* RO_INIT_MULTITHREADED */);
            if (hr < 0 && hr != unchecked((int)0x80010106))
                Marshal.ThrowExceptionForHR(hr);
        }
    }

    /// <summary>An HSTRING with a lifetime.</summary>
    private sealed class HString : IDisposable
    {
        public IntPtr Pointer { get; private set; }

        public HString(string value)
        {
            var hr = Native.WindowsCreateString(value, (uint)value.Length, out var pointer);
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);

            Pointer = pointer;
        }

        /// <summary>Reads and frees an HSTRING handed to us as an out parameter.</summary>
        public static string Read(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return string.Empty;

            try
            {
                var buffer = Native.WindowsGetStringRawBuffer(handle, out var length);
                return Marshal.PtrToStringUni(buffer, (int)length) ?? string.Empty;
            }
            finally
            {
                Native.WindowsDeleteString(handle);
            }
        }

        public void Dispose()
        {
            if (Pointer != IntPtr.Zero)
            {
                Native.WindowsDeleteString(Pointer);
                Pointer = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// An <c>IIterable&lt;String&gt;</c> of our own, for the two string lists
    /// the Store wants handed in. There is no activatable WinRT class that
    /// would do this for us, so the object is provided from here.
    /// </summary>
    private sealed class HStringList : IDisposable
    {
        public IntPtr Pointer { get; private set; }

        public HStringList(params string[] items)
        {
            Pointer = Com.Expose(new StringIterable(items), typeof(IIterableOfString).GUID);
        }

        public void Dispose()
        {
            if (Pointer != IntPtr.Zero)
            {
                Marshal.Release(Pointer);
                Pointer = IntPtr.Zero;
            }
        }
    }

    [GeneratedComClass]
    private sealed partial class StringIterable(string[] items) : IIterableOfString
    {
        public void GetIids(out uint count, out IntPtr iids)
        {
            count = 0;
            iids = IntPtr.Zero;
        }

        public void GetRuntimeClassName(out IntPtr name) => name = IntPtr.Zero;

        public void GetTrustLevel(out int level) => level = 0;

        public void First(out IntPtr iterator) =>
            iterator = Com.Expose(new StringIterator(items), typeof(IIteratorOfString).GUID);
    }

    [GeneratedComClass]
    private sealed partial class StringIterator(string[] items) : IIteratorOfString
    {
        private int _index;

        public void GetIids(out uint count, out IntPtr iids)
        {
            count = 0;
            iids = IntPtr.Zero;
        }

        public void GetRuntimeClassName(out IntPtr name) => name = IntPtr.Zero;

        public void GetTrustLevel(out int level) => level = 0;

        public void get_Current(out IntPtr current)
        {
            if (_index >= items.Length)
                throw new COMException("No current element.", unchecked((int)0x8000000B) /* E_BOUNDS */);

            current = Handle(items[_index]);
        }

        public void get_HasCurrent(out byte has) => has = _index < items.Length ? (byte)1 : (byte)0;

        public void MoveNext(out byte has)
        {
            if (_index < items.Length)
                _index++;

            has = _index < items.Length ? (byte)1 : (byte)0;
        }

        public unsafe void GetMany(uint capacity, IntPtr buffer, out uint actual)
        {
            var slots = (IntPtr*)buffer;
            uint written = 0;

            while (written < capacity && _index < items.Length)
                slots[written++] = Handle(items[_index++]);

            actual = written;
        }

        /// <summary>A fresh HSTRING the caller owns.</summary>
        private static IntPtr Handle(string value)
        {
            var hr = Native.WindowsCreateString(value, (uint)value.Length, out var handle);
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);

            return handle;
        }
    }

    private static partial class Native
    {
        [LibraryImport("combase.dll")]
        public static partial int RoInitialize(int initType);

        [LibraryImport("combase.dll")]
        public static partial int RoGetActivationFactory(IntPtr className, in Guid iid, out IntPtr factory);

        [LibraryImport("combase.dll")]
        public static partial int RoActivateInstance(IntPtr className, out IntPtr instance);

        [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
        public static partial int WindowsCreateString(string source, uint length, out IntPtr hstring);

        [LibraryImport("combase.dll")]
        public static partial int WindowsDeleteString(IntPtr hstring);

        [LibraryImport("combase.dll")]
        public static partial IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
    }

    // ---------------------------------------------------------------
    // Interfaces, in vtable order from the Windows SDK headers.
    //
    // Only the members that are called carry real parameter lists. The ones
    // before them exist to hold their slot - a vtable is positional - and are
    // declared without parameters so that nobody is tempted to call them.
    // Strings cross as HSTRING handles (IntPtr), interfaces as raw pointers,
    // and WinRT booleans as the single byte they are.
    // ---------------------------------------------------------------

    [GeneratedComInterface]
    [Guid("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90")]
    internal partial interface IInspectable
    {
        void GetIids(out uint count, out IntPtr iids);
        void GetRuntimeClassName(out IntPtr name);
        void GetTrustLevel(out int level);
    }

    [GeneratedComInterface]
    [Guid("9C06EE5F-15C0-4E72-9330-D6191CEBD19C")]
    internal partial interface IStoreContextStatics : IInspectable
    {
        void GetDefault(out IntPtr context);
    }

    [GeneratedComInterface]
    [Guid("AC98B6BE-F4FD-4912-BABD-5035E5E8BCAB")]
    internal partial interface IStoreContext : IInspectable
    {
        void get_User();
        void add_OfflineLicensesChanged();
        void remove_OfflineLicensesChanged();
        void GetCustomerPurchaseIdAsync();
        void GetCustomerCollectionsIdAsync();
        void GetAppLicenseAsync();
        void GetStoreProductForCurrentAppAsync();
        void GetStoreProductsAsync(IntPtr productKinds, IntPtr storeIds, out IntPtr operation);
    }

    [GeneratedComInterface]
    [Guid("18BC54DA-7BD9-452C-9116-3BBD06FFC63A")]
    internal partial interface IStoreContext2 : IInspectable
    {
        void FindStoreProductForPackageAsync(IntPtr productKinds, IntPtr package, out IntPtr operation);
    }

    /// <summary>IAsyncInfo, the part of every WinRT operation that says how it is going.</summary>
    [GeneratedComInterface]
    [Guid("00000036-0000-0000-C000-000000000046")]
    internal partial interface IAsyncInfo : IInspectable
    {
        void get_Id(out uint id);
        void get_Status(out int status);
        void get_ErrorCode(out int errorCode);
        void Cancel();
        void Close();
    }

    /// <summary>IAsyncOperation&lt;StoreProductQueryResult&gt;.</summary>
    [GeneratedComInterface]
    [Guid("9699E7BB-EA1F-5E03-9439-C80E6977B711")]
    internal partial interface IStoreProductQueryResultOperation : IInspectable
    {
        void put_Completed();
        void get_Completed();
        void GetResults(out IntPtr result);
    }

    /// <summary>IAsyncOperation&lt;StoreProductResult&gt;.</summary>
    [GeneratedComInterface]
    [Guid("9E61E86B-6AFB-50AE-AFC1-C59F545108DD")]
    internal partial interface IStoreProductResultOperation : IInspectable
    {
        void put_Completed();
        void get_Completed();
        void GetResults(out IntPtr result);
    }

    [GeneratedComInterface]
    [Guid("D805E6C5-D456-4FF6-8049-9076D5165F73")]
    internal partial interface IStoreProductQueryResult : IInspectable
    {
        void get_Products(out IntPtr products);
        void get_ExtendedError(out int error);
    }

    [GeneratedComInterface]
    [Guid("B7674F73-3C87-4EE1-8201-F428359BD3AF")]
    internal partial interface IStoreProductResult : IInspectable
    {
        void get_Product(out IntPtr product);
        void get_ExtendedError(out int error);
    }

    /// <summary>IMapView&lt;String, StoreProduct&gt;.</summary>
    [GeneratedComInterface]
    [Guid("DBAAC6E5-61A4-5C88-B5D8-3A3E161C3E4A")]
    internal partial interface IMapViewOfStoreProduct : IInspectable
    {
        void Lookup(IntPtr key, out IntPtr value);
        void get_Size(out uint size);
        void HasKey(IntPtr key, out byte found);
    }

    [GeneratedComInterface]
    [Guid("320E2C52-D760-450A-A42B-67D1E901AC90")]
    internal partial interface IStoreProduct : IInspectable
    {
        void get_StoreId(out IntPtr storeId);
        void get_Language();
        void get_Title(out IntPtr title);
        void get_Description();
        void get_ProductKind();
        void get_HasDigitalDownload();
        void get_Keywords();
        void get_Images(out IntPtr images);
    }

    /// <summary>IVectorView&lt;StoreImage&gt;.</summary>
    [GeneratedComInterface]
    [Guid("7E1CEACE-82BD-5DB3-8F35-9BF0C88EF839")]
    internal partial interface IVectorViewOfStoreImage : IInspectable
    {
        void GetAt(uint index, out IntPtr item);
        void get_Size(out uint size);
    }

    [GeneratedComInterface]
    [Guid("081FD248-ADB4-4B64-A993-784789926ED5")]
    internal partial interface IStoreImage : IInspectable
    {
        void get_Uri(out IntPtr uri);
        void get_ImagePurposeTag(out IntPtr tag);
        void get_Width(out uint width);
        void get_Height(out uint height);
    }

    [GeneratedComInterface]
    [Guid("9E365E57-48B2-4160-956F-C7385120BBFC")]
    internal partial interface IUriRuntimeClass : IInspectable
    {
        void get_AbsoluteUri(out IntPtr uri);
    }

    [GeneratedComInterface]
    [Guid("9A7D4B65-5E8F-4FC7-A2E5-7F6925CB8B53")]
    internal partial interface IPackageManager : IInspectable
    {
        void AddPackageAsync();
        void UpdatePackageAsync();
        void RemovePackageAsync();
        void StagePackageAsync();
        void RegisterPackageAsync();
        void FindPackages();
        void FindPackagesByUserSecurityId();
        void FindPackagesByNamePublisher();
        void FindPackagesByUserSecurityIdNamePublisher();
        void FindUsers();
        void SetPackageState();
        void FindPackageByPackageFullName();
        void CleanupPackageForUserAsync();
        void FindPackagesByPackageFamilyName();
        void FindPackagesByUserSecurityIdPackageFamilyName(IntPtr userSecurityId, IntPtr familyName, out IntPtr packages);
    }

    /// <summary>IIterable&lt;Package&gt;.</summary>
    [GeneratedComInterface]
    [Guid("69AD6AA7-0C49-5F27-A5EB-EF4D59467B6D")]
    internal partial interface IIterableOfPackage : IInspectable
    {
        void First(out IntPtr iterator);
    }

    /// <summary>IIterator&lt;Package&gt;.</summary>
    [GeneratedComInterface]
    [Guid("0217F069-025C-5EE6-A87F-E782E3B623AE")]
    internal partial interface IIteratorOfPackage : IInspectable
    {
        void get_Current(out IntPtr current);
        void get_HasCurrent(out byte has);
    }

    /// <summary>IIterable&lt;String&gt;, the one interface here that we implement rather than call.</summary>
    [GeneratedComInterface]
    [Guid("E2FCC7C1-3BFC-5A0B-B2B0-72E769D1CB7E")]
    internal partial interface IIterableOfString : IInspectable
    {
        void First(out IntPtr iterator);
    }

    /// <summary>IIterator&lt;String&gt;, likewise.</summary>
    [GeneratedComInterface]
    [Guid("8C304EBB-6615-50A4-8829-879ECD443236")]
    internal partial interface IIteratorOfString : IInspectable
    {
        void get_Current(out IntPtr current);
        void get_HasCurrent(out byte has);
        void MoveNext(out byte has);
        void GetMany(uint capacity, IntPtr items, out uint actual);
    }
}

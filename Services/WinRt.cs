using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AppCenter.Services;

/// <summary>
/// The plumbing under the hand-written WinRT interop: activation, the
/// wrappers that make a raw interface pointer callable, and HSTRINGs. The
/// Store listing and the winget reads each declare the interfaces they call
/// on top of this.
///
/// Hand-written rather than the WinRT projection, which would mean a Windows
/// SDK target framework and a twenty-megabyte assembly for a handful of
/// method calls. Interfaces are declared in vtable order from the SDK headers
/// or the winmd; the parameterised ones (IVectorView and so on) carry the
/// IIDs the WinRT signature hash produces for their type arguments.
/// </summary>
internal static partial class WinRt
{
    internal static class Com
    {
        private static readonly StrategyBasedComWrappers Wrappers = new();

        /// <summary>
        /// Takes ownership of a raw interface pointer and hands back a wrapper
        /// that can be cast to any of the interfaces declared on top of this.
        /// The wrapper holds its own references, so the caller's one is
        /// released here.
        /// </summary>
        public static T Wrap<T>(IntPtr pointer) where T : class
        {
            if (pointer == IntPtr.Zero)
                throw new InvalidOperationException("No object came back where one was expected.");

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
        /// A new instance of a COM class from its own server process, as a raw
        /// pointer to the interface asked for. Released by the caller, or
        /// handed to <see cref="Wrap{T}"/>, which does that.
        ///
        /// From an elevated process the lower-trust flag is needed as well: a
        /// class registered per user - packaged COM, which is how winget
        /// registers its server - is invisible to a high-integrity caller
        /// otherwise, and activation fails with REGDB_E_CLASSNOTREG.
        /// </summary>
        public static IntPtr Create(Guid clsid, Guid iid, bool allowLowerTrust = false)
        {
            const uint LocalServer = 0x4;
            const uint AllowLowerTrustRegistration = 0x4000000;

            var context = LocalServer | (allowLowerTrust ? AllowLowerTrustRegistration : 0);
            var hr = Native.CoCreateInstance(in clsid, IntPtr.Zero, context, in iid, out var pointer);
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);

            return pointer;
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
    internal sealed class HString : IDisposable
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

    internal static partial class Native
    {
        [LibraryImport("combase.dll")]
        public static partial int RoInitialize(int initType);

        [LibraryImport("combase.dll")]
        public static partial int RoGetActivationFactory(IntPtr className, in Guid iid, out IntPtr factory);

        [LibraryImport("combase.dll")]
        public static partial int RoActivateInstance(IntPtr className, out IntPtr instance);

        [LibraryImport("ole32.dll")]
        public static partial int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

        [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
        public static partial int WindowsCreateString(string source, uint length, out IntPtr hstring);

        [LibraryImport("combase.dll")]
        public static partial int WindowsDeleteString(IntPtr hstring);

        [LibraryImport("combase.dll")]
        public static partial IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
    }

    /// <summary>
    /// The root of every WinRT interface: three slots after IUnknown's three.
    /// Declared once here; the interfaces that build on it follow the same
    /// rules - only the members that are called carry real parameter lists,
    /// the ones before them hold their slot and are declared without
    /// parameters so that nobody is tempted to call them, strings cross as
    /// HSTRING handles (IntPtr), interfaces as raw pointers, enumerations as
    /// the int they are, and WinRT booleans as the single byte they are.
    /// </summary>
    [GeneratedComInterface]
    [Guid("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90")]
    internal partial interface IInspectable
    {
        void GetIids(out uint count, out IntPtr iids);
        void GetRuntimeClassName(out IntPtr name);
        void GetTrustLevel(out int level);
    }
}

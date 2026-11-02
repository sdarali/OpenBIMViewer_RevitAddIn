using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBimViewer.Collab.Native
{
    /// <summary>
    /// IfcCollabCore.dll (native/IfcCollabCoreBridge, collab_bridge.h): the IFC Collab Core behind a
    /// C interface. Strings cross as UTF-8; everything is called on the logic thread.
    /// </summary>
    internal static class NativeMethods
    {
        public const string Dll = "IfcCollabCore";
        /// <summary>COLLAB_BRIDGE_ABI_VERSION in collab_bridge.h.</summary>
        public const int AbiVersion = 1;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void SendFn(IntPtr context, ulong requestId, IntPtr requestJson, IntPtr body, UIntPtr bodySize);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void IdFn(IntPtr context, ulong id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void PostDelayedFn(IntPtr context, ulong timerId, double delaySeconds);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void TextFn(IntPtr context, IntPtr json);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void LogFn(IntPtr context, int level, IntPtr message);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ResultFn(IntPtr context, ulong callId, IntPtr resultJson, IntPtr data, UIntPtr dataSize);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void OnlineFn(IntPtr context, int online);

        [StructLayout(LayoutKind.Sequential)]
        public struct Host
        {
            public IntPtr Context;
            public IntPtr Send;
            public IntPtr Cancel;
            public IntPtr PostDelayed;
            public IntPtr CancelTimer;
            public IntPtr SaveSession;
            public IntPtr Log;
            public IntPtr Result;
            public IntPtr OnlineChanged;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "collab_bridge_abi_version")]
        public static extern int AbiVersionOf();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "collab_bridge_create")]
        public static extern IntPtr Create(ref Host host, byte[] configJson, byte[]? sessionJson, byte[] cacheDir);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "collab_bridge_destroy")]
        public static extern void Destroy(IntPtr bridge);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "collab_bridge_complete_request")]
        public static extern void CompleteRequest(IntPtr bridge, ulong requestId, byte[] responseJson, byte[]? body, UIntPtr bodySize);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "collab_bridge_fire_timer")]
        public static extern void FireTimer(IntPtr bridge, ulong timerId);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "collab_bridge_call")]
        public static extern void Call(IntPtr bridge, ulong callId, byte[] op, byte[] argsJson);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "collab_bridge_query")]
        public static extern IntPtr Query(IntPtr bridge, byte[] op, byte[] argsJson);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        private static readonly object LoadLock = new object();
        private static bool _loaded;

        /// <summary>
        /// Loads IfcCollabCore.dll from beside this assembly. Revit only searches its own folder
        /// for native DLLs, so the add-in's copy has to be loaded by path before the first call;
        /// [DllImport] then binds to the module already in the process.
        /// </summary>
        public static void EnsureLoaded(string? directory = null)
        {
            lock (LoadLock)
            {
                if (_loaded) return;
                var path = Path.Combine(directory ?? FindDirectory(), Dll + ".dll");
                if (!File.Exists(path)) throw new FileNotFoundException($"{Dll}.dll is missing from {Path.GetDirectoryName(path)}.", path);
                if (LoadLibraryW(path) == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not load {path}");
                }
                var abi = AbiVersionOf();
                if (abi != AbiVersion)
                {
                    throw new InvalidOperationException($"{Dll}.dll has interface version {abi}; this add-in needs {AbiVersion}. Reinstall the add-in.");
                }
                _loaded = true;
            }
        }

        /// <summary>
        /// This assembly's folder. On .NET Framework a host may shadow-copy assemblies (test runners
        /// do), leaving the native DLL behind at the original location, which CodeBase still names.
        /// </summary>
        private static string FindDirectory()
        {
            var assembly = typeof(NativeMethods).Assembly;
            var candidates = new System.Collections.Generic.List<string?> { Path.GetDirectoryName(assembly.Location) };
            try
            {
#pragma warning disable SYSLIB0012 // CodeBase: only consulted where it still means something
                if (Uri.TryCreate(assembly.CodeBase, UriKind.Absolute, out var codeBase) && codeBase.IsFile)
                    candidates.Add(Path.GetDirectoryName(codeBase.LocalPath));
#pragma warning restore SYSLIB0012
            }
            catch (NotSupportedException)
            {
            }
            candidates.Add(AppDomain.CurrentDomain.BaseDirectory);
            foreach (var dir in candidates)
            {
                if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, Dll + ".dll"))) return dir!;
            }
            return candidates[0] ?? "";
        }

        /// <summary>Null-terminated UTF-8.</summary>
        public static byte[] Utf8(string? s)
        {
            var count = Encoding.UTF8.GetByteCount(s ?? "");
            var bytes = new byte[count + 1];
            Encoding.UTF8.GetBytes(s ?? "", 0, (s ?? "").Length, bytes, 0);
            return bytes;
        }

        public static string FromUtf8(IntPtr p)
        {
            if (p == IntPtr.Zero) return "";
            var length = 0;
            while (Marshal.ReadByte(p, length) != 0) length++;
            var bytes = new byte[length];
            Marshal.Copy(p, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        public static byte[] Copy(IntPtr data, UIntPtr size)
        {
            var length = checked((int)size.ToUInt64());
            var bytes = new byte[length];
            if (length > 0) Marshal.Copy(data, bytes, 0, length);
            return bytes;
        }
    }
}

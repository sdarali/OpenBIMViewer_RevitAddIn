using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenBimViewer.Collab
{
    /// <summary>
    /// Windows DPAPI (CryptProtectData) for the current Windows user, called directly so the add-in
    /// needs no System.Security.Cryptography.ProtectedData package on .NET 8/10.
    /// </summary>
    public static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        private const int UiForbidden = 0x1;

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy, IntPtr reserved,
            IntPtr prompt, int flags, out DataBlob output);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved,
            IntPtr prompt, int flags, out DataBlob output);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);

        public static byte[] Protect(byte[] data, byte[] entropy) => Run(data, entropy, protect: true);

        /// <summary>Throws <see cref="Win32Exception"/> for data another Windows user (or another entropy) protected.</summary>
        public static byte[] Unprotect(byte[] data, byte[] entropy) => Run(data, entropy, protect: false);

        private static byte[] Run(byte[] data, byte[] entropy, bool protect)
        {
            var dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
            var entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
            try
            {
                var input = new DataBlob { Size = data.Length, Data = dataHandle.AddrOfPinnedObject() };
                var salt = new DataBlob { Size = entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
                var ok = protect
                    ? CryptProtectData(ref input, null, ref salt, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref salt, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    var result = new byte[output.Size];
                    Marshal.Copy(output.Data, result, 0, output.Size);
                    return result;
                }
                finally
                {
                    LocalFree(output.Data);
                }
            }
            finally
            {
                dataHandle.Free();
                entropyHandle.Free();
            }
        }
    }
}

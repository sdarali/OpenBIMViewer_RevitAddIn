using System;

namespace OpenBimViewer.Collab
{
    /// <summary>
    /// IFC GlobalIds: a 128-bit GUID written as 22 characters of IFC's base-64 alphabet. Revit's IFC
    /// exporter derives each element's GlobalId this way from its export GUID, so the add-in can
    /// name selected elements the way the server's IFC models do.
    /// </summary>
    public static class IfcGuid
    {
        private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";

        public static string Encode(Guid guid)
        {
            // The GUID's bytes in their written (big-endian) order.
            var b = guid.ToByteArray();
            var bytes = new byte[]
            {
                b[3], b[2], b[1], b[0], b[5], b[4], b[7], b[6],
                b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15],
            };
            var chars = new char[22];
            // First 2 bits on their own, then 126 bits in 6-bit groups: 1 + 21 characters.
            chars[0] = Alphabet[bytes[0] >> 6];
            var acc = bytes[0] & 0x3F;
            var bits = 6;
            var pos = 1;
            for (var i = 1; i < 16; i++)
            {
                acc = (acc << 8) | bytes[i];
                bits += 8;
                while (bits >= 6)
                {
                    bits -= 6;
                    chars[pos++] = Alphabet[(acc >> bits) & 0x3F];
                }
                acc &= (1 << bits) - 1;
            }
            return new string(chars, 0, pos);
        }

        public static bool IsValid(string? s)
        {
            if (s == null || s.Length != 22 || Alphabet.IndexOf(s[0]) > 3) return false;
            foreach (var c in s)
            {
                if (Alphabet.IndexOf(c) < 0) return false;
            }
            return true;
        }
    }
}

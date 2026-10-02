using System;
using System.IO;
using System.IO.Compression;

namespace LightSide
{
    /// <summary>
    /// Pure-managed compression for font-file bytes (TTF/OTF), used to shrink the resident and
    /// on-disk footprint of <see cref="UniTextFont.fontData"/>. Deflate (<see cref="DeflateStream"/>)
    /// is in the BCL on every Unity backend (Mono + IL2CPP, all platforms) and touches NO native
    /// code, so this changes nothing about the shaping/rasterization DLL and therefore nothing about
    /// the CI cross-platform reference results.
    /// </summary>
    /// <remarks>
    /// Format: a 12-byte header <c>[ 'U','T','F','Z', version:byte, level:byte, reserved:2, rawLen:int32-LE ]</c>
    /// followed by the raw-deflate stream. The header lets <see cref="IsCompressed"/> recognise our
    /// container (so a plain TTF/OTF is never mistaken for one — TTF starts with 0x00010000 / 'OTTO'
    /// / 'true' / 'ttcf', none of which is our magic) and lets <see cref="Decompress"/> pre-size the
    /// output buffer for a single allocation. TTF/OTF typically compress to ~55-65 % of raw.
    /// </remarks>
    public static class FontCompression
    {
        // 'U' 'T' 'F' 'Z'
        private static readonly byte[] Magic = { 0x55, 0x54, 0x46, 0x5A };
        private const int HeaderLength = 12;
        private const byte FormatVersion = 1;

        /// <summary>True if <paramref name="data"/> carries the UTFZ compressed-font header.</summary>
        public static bool IsCompressed(byte[] data)
        {
            if (data == null || data.Length < HeaderLength) return false;
            return data[0] == Magic[0] && data[1] == Magic[1] && data[2] == Magic[2] && data[3] == Magic[3];
        }

        /// <summary>Raw (decompressed) length recorded in a UTFZ container, or -1 if not one.</summary>
        public static int RawLength(byte[] data)
        {
            if (!IsCompressed(data)) return -1;
            return data[8] | (data[9] << 8) | (data[10] << 16) | (data[11] << 24);
        }

        /// <summary>
        /// Compresses raw font bytes into a UTFZ container. Returns the original array unchanged when
        /// it is null/empty or already compressed (idempotent), or when compression would not shrink
        /// it (keeps the smaller of the two, so this never makes a font bigger).
        /// </summary>
        public static byte[] Compress(byte[] raw, CompressionLevel level = CompressionLevel.Optimal)
        {
            if (raw == null || raw.Length == 0) return raw;
            if (IsCompressed(raw)) return raw;

            byte[] deflated;
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, level, leaveOpen: true))
                    ds.Write(raw, 0, raw.Length);
                deflated = ms.ToArray();
            }

            // Only adopt compression if the container is actually smaller than the raw bytes.
            if (deflated.Length + HeaderLength >= raw.Length)
                return raw;

            var outBytes = new byte[HeaderLength + deflated.Length];
            Buffer.BlockCopy(Magic, 0, outBytes, 0, 4);
            outBytes[4] = FormatVersion;
            outBytes[5] = (byte)level;
            outBytes[6] = 0; outBytes[7] = 0;
            int n = raw.Length;
            outBytes[8] = (byte)(n & 0xFF);
            outBytes[9] = (byte)((n >> 8) & 0xFF);
            outBytes[10] = (byte)((n >> 16) & 0xFF);
            outBytes[11] = (byte)((n >> 24) & 0xFF);
            Buffer.BlockCopy(deflated, 0, outBytes, HeaderLength, deflated.Length);
            return outBytes;
        }

        /// <summary>
        /// Decompresses a UTFZ container back to the exact original raw bytes. Returns the input
        /// unchanged when it is null/empty or NOT a UTFZ container (so passing already-raw font bytes
        /// is safe and transparent). Throws <see cref="InvalidDataException"/> on a corrupt container.
        /// </summary>
        public static byte[] Decompress(byte[] data)
        {
            if (data == null || data.Length == 0) return data;
            if (!IsCompressed(data)) return data;

            int rawLen = RawLength(data);
            if (rawLen < 0) throw new InvalidDataException("UTFZ: negative raw length.");

            var raw = new byte[rawLen];
            using (var ms = new MemoryStream(data, HeaderLength, data.Length - HeaderLength, writable: false))
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
            {
                int off = 0;
                while (off < rawLen)
                {
                    int read = ds.Read(raw, off, rawLen - off);
                    if (read <= 0) break;
                    off += read;
                }
                if (off != rawLen)
                    throw new InvalidDataException($"UTFZ: expected {rawLen} bytes, decoded {off}.");
            }
            return raw;
        }
    }
}

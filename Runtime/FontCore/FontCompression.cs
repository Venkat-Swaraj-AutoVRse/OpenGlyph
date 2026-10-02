using System;
using System.IO;
using System.IO.Compression;

namespace LightSide
{
    /// <summary>
    /// Pure-managed compression for font-file bytes (TTF/OTF), used to shrink the resident and
    /// on-disk footprint of <see cref="UniTextFont.fontData"/>. No native code is involved, so this
    /// changes nothing about the shaping/rasterization DLL and therefore nothing about the CI
    /// cross-platform reference results.
    /// </summary>
    /// <remarks>
    /// <para><b>Codec choice (cross-platform safety).</b> Two BCL codecs are supported:
    /// <see cref="DeflateStream"/> (codec 0) and <see cref="BrotliStream"/> (codec 1). Deflate is
    /// present on EVERY Unity backend (Mono + IL2CPP, all platforms) and is the DEFAULT — a font
    /// compressed with the default codec decodes on every target. Brotli gives a better ratio but is
    /// NOT guaranteed on every IL2CPP player (notably some Android configurations), so it is used
    /// ONLY when the caller opts in (<c>preferBrotli: true</c>) AND this runtime proves it can both
    /// encode and decode Brotli (<see cref="BrotliAvailable"/>, a cached round-trip probe). This makes
    /// the invariant the task requires true by construction: <b>edit-time compression never emits a
    /// container the compressing runtime cannot itself decode</b>, and a container is only emitted in
    /// Brotli when Brotli round-trips here.</para>
    /// <para><b>Decoding never crashes.</b> <see cref="Decompress"/> validates the codec id and, if a
    /// container claims a codec this runtime cannot provide, throws a caught-and-reported
    /// <see cref="InvalidDataException"/> rather than letting a missing-type/native fault escape. The
    /// <see cref="UniTextFont"/> load path treats that as "keep the stored bytes" rather than a hard
    /// failure. In practice the default-Deflate policy means a shipped asset is Deflate, which every
    /// platform decodes.</para>
    /// <para><b>Format:</b> a 12-byte header
    /// <c>[ 'U','T','F','Z', version:byte, level:byte, codec:byte, reserved:1, rawLen:int32-LE ]</c>
    /// followed by the codec stream. The header lets <see cref="IsCompressed"/> recognise our
    /// container (a plain TTF/OTF never collides — TTF starts with 0x00010000 / 'OTTO' / 'true' /
    /// 'ttcf', none of which is our magic) and lets <see cref="Decompress"/> pre-size the output for
    /// a single allocation.</para>
    /// </remarks>
    public static class FontCompression
    {
        // 'U' 'T' 'F' 'Z'
        private static readonly byte[] Magic = { 0x55, 0x54, 0x46, 0x5A };
        private const int HeaderLength = 12;
        private const byte FormatVersion = 1;

        private const byte CodecDeflate = 0;
        private const byte CodecBrotli = 1;

        private static int _brotliAvailable = -1; // -1 unknown, 0 no, 1 yes (cached probe result)

        /// <summary>
        /// True iff THIS runtime can both compress and decompress Brotli via the BCL
        /// <see cref="BrotliStream"/>. Probed once (a tiny round-trip) and cached. On a backend where
        /// the Brotli type or its native backend is absent, the probe fails safely and this is false,
        /// so compression falls back to Deflate and no un-decodable container is ever produced.
        /// </summary>
        public static bool BrotliAvailable
        {
            get
            {
                if (_brotliAvailable >= 0) return _brotliAvailable == 1;
                bool ok = false;
                try
                {
                    var probe = new byte[64];
                    for (int i = 0; i < probe.Length; i++) probe[i] = (byte)(i * 7);
                    byte[] enc;
                    using (var ms = new MemoryStream())
                    {
                        using (var bs = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                            bs.Write(probe, 0, probe.Length);
                        enc = ms.ToArray();
                    }
                    var dec = new byte[probe.Length];
                    using (var ms = new MemoryStream(enc, writable: false))
                    using (var bs = new BrotliStream(ms, CompressionMode.Decompress))
                    {
                        int off = 0, r;
                        while (off < dec.Length && (r = bs.Read(dec, off, dec.Length - off)) > 0) off += r;
                        ok = off == probe.Length;
                    }
                    if (ok)
                        for (int i = 0; i < probe.Length; i++)
                            if (dec[i] != probe[i]) { ok = false; break; }
                }
                catch { ok = false; }
                _brotliAvailable = ok ? 1 : 0;
                return ok;
            }
        }

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

        /// <summary>Codec id recorded in a UTFZ container (0=Deflate, 1=Brotli), or -1 if not one.</summary>
        public static int CodecId(byte[] data) => IsCompressed(data) ? data[6] : -1;

        /// <summary>
        /// Compresses raw font bytes into a UTFZ container. Returns the original array unchanged when
        /// it is null/empty, already compressed (idempotent), or when compression would not shrink it
        /// (keeps the smaller of the two, so this never makes a font bigger).
        /// </summary>
        /// <param name="raw">Raw font bytes.</param>
        /// <param name="level">Compression level.</param>
        /// <param name="preferBrotli">
        /// When true AND <see cref="BrotliAvailable"/> is true on THIS runtime, encode with Brotli
        /// (better ratio). Default FALSE: encode with Deflate, which every Unity player can decode.
        /// Only pass true when every target platform is known to decode Brotli — the probe still
        /// guards against emitting a Brotli container a runtime cannot read.
        /// </param>
        public static byte[] Compress(byte[] raw, CompressionLevel level = CompressionLevel.Optimal, bool preferBrotli = false)
        {
            if (raw == null || raw.Length == 0) return raw;
            if (IsCompressed(raw)) return raw;

            byte codec;
            byte[] body;

            // Brotli ONLY when asked AND provably decodable here, so a shipped container is never
            // stranded on a platform that cannot decode it. Otherwise Deflate (universal).
            if (preferBrotli && BrotliAvailable)
            {
                codec = CodecBrotli;
                using var ms = new MemoryStream();
                using (var bs = new BrotliStream(ms, level, leaveOpen: true)) bs.Write(raw, 0, raw.Length);
                body = ms.ToArray();
            }
            else
            {
                codec = CodecDeflate;
                using var ms = new MemoryStream();
                using (var ds = new DeflateStream(ms, level, leaveOpen: true)) ds.Write(raw, 0, raw.Length);
                body = ms.ToArray();
            }

            // Only adopt compression if the container is actually smaller than the raw bytes.
            if (body.Length + HeaderLength >= raw.Length)
                return raw;

            var outBytes = new byte[HeaderLength + body.Length];
            Buffer.BlockCopy(Magic, 0, outBytes, 0, 4);
            outBytes[4] = FormatVersion;
            outBytes[5] = (byte)level;
            outBytes[6] = codec;   // 0=Deflate, 1=Brotli
            outBytes[7] = 0;
            int n = raw.Length;
            outBytes[8] = (byte)(n & 0xFF);
            outBytes[9] = (byte)((n >> 8) & 0xFF);
            outBytes[10] = (byte)((n >> 16) & 0xFF);
            outBytes[11] = (byte)((n >> 24) & 0xFF);
            Buffer.BlockCopy(body, 0, outBytes, HeaderLength, body.Length);
            return outBytes;
        }

        /// <summary>
        /// Decompresses a UTFZ container back to the exact original raw bytes. Returns the input
        /// unchanged when it is null/empty or NOT a UTFZ container (so passing already-raw font bytes
        /// is safe and transparent). NEVER crashes: a corrupt container, an unknown codec id, or a
        /// Brotli container on a runtime without Brotli all raise a caught
        /// <see cref="InvalidDataException"/> that the caller handles by keeping the stored bytes.
        /// </summary>
        public static byte[] Decompress(byte[] data)
        {
            if (data == null || data.Length == 0) return data;
            if (!IsCompressed(data)) return data;

            int rawLen = RawLength(data);
            if (rawLen < 0) throw new InvalidDataException("UTFZ: negative raw length.");
            byte codec = data[6];

            if (codec == CodecBrotli && !BrotliAvailable)
                throw new InvalidDataException(
                    "UTFZ: container is Brotli-coded but this runtime has no Brotli decoder. " +
                    "Re-compress with the default (Deflate) codec so every target platform can decode it.");
            if (codec != CodecDeflate && codec != CodecBrotli)
                throw new InvalidDataException($"UTFZ: unknown codec id {codec}.");

            var raw = new byte[rawLen];
            using (var ms = new MemoryStream(data, HeaderLength, data.Length - HeaderLength, writable: false))
            using (Stream ds = codec == CodecBrotli
                ? new BrotliStream(ms, CompressionMode.Decompress)
                : new DeflateStream(ms, CompressionMode.Decompress))
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

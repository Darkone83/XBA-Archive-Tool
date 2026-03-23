// XbaZstd.cs — Zstandard compression codec for XBA V3 blocks
//
// Requires NuGet package: ZstdSharp.Port
//   <PackageReference Include="ZstdSharp.Port" Version="0.8.7" />
//
// ZstdSharp.Port is a pure C# port of zstd v1.5.7 — no native DLL,
// no P/Invoke, no deployment dependencies. Full managed code.
//
// Zstd advantages for archival:
//   - Consistently beats both Deflate and Brotli on general binary data
//   - Very fast decompression (~500 MB/s) — best of any codec here
//   - Level 9 gives near-maximum ratio at reasonable compression speed
//   - Used by modern game engines for asset compression precisely because
//     it handles unknown binary content better than older LZ77 variants
//   - Framing: ZstdSharp Wrap/Unwrap produces a self-contained zstd frame
//     with magic, frame header, and checksum — no extra framing needed
//
// Future Xbox-side decoder:
//   The zstd reference decompressor (zstd_decompress.c) is ~2000 lines of
//   portable C89-compatible C with no OS dependencies. Feasible for Xbox.

using System;
using System.IO;
using ZstdSharp;

namespace XbaTool
{
    internal static class XbaZstd
    {
        // CompLimit mirrors XbaCodec.CompLimit — must beat this to win.
        private const double CompLimit = 0.98;

        // Compression level 15: strong ratio, reasonable speed for archival.
        // Level 9 is the speed/ratio elbow; level 15 gives meaningful additional
        // ratio improvement at ~4x slower compression — acceptable since Zstd
        // only runs on blocks where it wins the contest.
        // Level 19 (max normal) and 20-22 (ultra) are too slow for interactive use.
        private const int Level = 15;

        // ── CompressZstd ──────────────────────────────────────────────────────
        // Compress a single block with Zstd at level 9.
        // Returns null when the result does not beat the CompLimit threshold.

        public static byte[]? CompressZstd(byte[] block)
        {
            if (block.Length == 0) return Array.Empty<byte>();

            using var compressor = new Compressor(Level);
            byte[] result = compressor.Wrap(block).ToArray();

            if (result.Length >= block.Length * CompLimit) return null;
            return result;
        }

        // ── DecompressZstd ────────────────────────────────────────────────────
        // Decompress a Zstd-compressed block produced by CompressZstd.

        public static byte[] DecompressZstd(byte[] data, int uncompSize)
        {
            if (data.Length == 0) return Array.Empty<byte>();

            using var decompressor = new Decompressor();
            byte[] result = decompressor.Unwrap(data, uncompSize).ToArray();

            if (result.Length != uncompSize)
                throw new InvalidDataException(
                    $"Zstd decompress size mismatch: expected {uncompSize}, got {result.Length}");

            return result;
        }
    }
}
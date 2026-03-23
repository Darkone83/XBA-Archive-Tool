// XbaBrotli.cs — Brotli compression codec for XBA V3 blocks
//
// Uses System.IO.Compression.BrotliEncoder (built-in .NET) for direct
// quality control — no external dependencies.
//
// Quality 7: sits at the ratio/speed elbow for block compression.
//   Quality 5 (Optimal): fast, beats Deflate, good starting point.
//   Quality 7: ~5-8% better ratio than quality 5, ~2x slower — worth it
//              for archival since we only run Brotli when it wins the contest.
//   Quality 11 (SmallestSize): maximum ratio, 10-50x slower than quality 5.
//              Too slow for interactive use on 256 KB blocks.
//
// Block format stored in XBA V3:
//   Raw Brotli-compressed bytes. V3 stores comp_size and uncomp_size per
//   block so no additional framing is needed.
//
// Future Xbox-side decoder:
//   The brotli reference decoder is ~3000 lines of portable C with no OS
//   dependencies — feasible as a C89 Xbox homebrew implementation.

using System;
using System.IO;
using System.IO.Compression;

namespace XbaTool
{
    internal static class XbaBrotli
    {
        // CompLimit mirrors XbaCodec.CompLimit — must beat this to win.
        private const double CompLimit = 0.98;

        // Quality 7: best ratio/speed balance for archival block compression.
        private const int Quality = 7;

        // Window size 22 = 4 MB — larger than our 256 KB blocks so the full
        // block is always within the compression window.
        private const int WindowBits = 22;

        // ── CompressBrotli ────────────────────────────────────────────────────
        // Compress a single block with Brotli at quality 7.
        // Returns null when the result does not beat the CompLimit threshold.

        public static byte[]? CompressBrotli(byte[] block)
        {
            if (block.Length == 0) return Array.Empty<byte>();

            // BrotliEncoder.TryCompress with quality/window control.
            // Output buffer worst-case is slightly larger than input.
            int maxOut = block.Length + (block.Length >> 4) + 64;
            var outBuf = new byte[maxOut];

            if (!BrotliEncoder.TryCompress(block, outBuf, out int bytesWritten, Quality, WindowBits))
            {
                // Fallback: block is incompressible or output buffer too small.
                return null;
            }

            if (bytesWritten >= block.Length * CompLimit) return null;

            var result = new byte[bytesWritten];
            Array.Copy(outBuf, result, bytesWritten);
            return result;
        }

        // ── DecompressBrotli ──────────────────────────────────────────────────
        // Decompress a Brotli-compressed block produced by CompressBrotli.

        public static byte[] DecompressBrotli(byte[] data, int uncompSize)
        {
            if (data.Length == 0) return Array.Empty<byte>();

            var out_ = new byte[uncompSize];
            if (!BrotliDecoder.TryDecompress(data, out_, out int bytesWritten))
                throw new InvalidDataException(
                    $"Brotli decompress failed for block of {data.Length} bytes");

            if (bytesWritten != uncompSize)
                throw new InvalidDataException(
                    $"Brotli decompress size mismatch: expected {uncompSize}, got {bytesWritten}");

            return out_;
        }
    }
}
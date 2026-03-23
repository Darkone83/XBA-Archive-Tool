// XbaV3.cs — XBA v3 format pack/unpack/list/test
//
// V3 format: files-only TOC, 256 KB blocks, raw Deflate + stride-split
// transform, whole-file pre-filters (x86, delta8/16/32/stereo).
// No directory entries in the stream -- paths encode hierarchy via backslash.
// CRC32 is of the final decoded + unfiltered data.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace XbaTool
{
    public static partial class XbaCodec
    {
        // ── V3 format constants ───────────────────────────────────────────────

        private static readonly byte[] MagicV3 = { (byte)'X', (byte)'B', (byte)'A', 0x03 };

        private const int V3BlockSize = 262144;

        // v3 filter flags
        private const byte V3FilterNone = 0x00;
        private const byte V3FilterX86 = 0x01;
        private const byte V3FilterDelta8 = 0x02;
        private const byte V3FilterDelta16 = 0x03;
        private const byte V3FilterDeltaStereo = 0x04;
        private const byte V3FilterDelta32 = 0x05;
        private const byte V3FilterSolid = 0xFF;

        // v3 block flags (stored/lz77/lzss same values as v2; deflate new)
        private const byte V3BlkStored = 0x00;
        private const byte V3BlkLz77 = 0x01;
        private const byte V3BlkLzss = 0x02;
        private const byte V3BlkDeflate = 0x03;
        // Strided block: stride-split then deflate.
        // Block payload: [1] stride (2/4/8) + raw DEFLATE of stride-split data.
        private const byte V3BlkStrided = 0x04;
        // Brotli block: raw Brotli-compressed bytes (BrotliStream Optimal).
        // Built-in .NET, no native DLL. Xbox-side: portable C89 brotli decoder.
        private const byte V3BlkBrotli = 0x05;
        // Zstd block: raw zstd frame (ZstdSharp.Port level 9).
        // Pure managed C#, no native DLL. Xbox-side: portable C89 zstd decoder.
        private const byte V3BlkZstd = 0x06;

        // ── Variable block size ───────────────────────────────────────────────
        // Block size is chosen per-file to maximise codec window utilisation.
        //   Small files (≤ 64 KB)  → single block  (whole file in one block)
        //   Medium files (≤ 2 MB)  → 64 KB blocks  (full 32 KB window coverage)
        //   Large files (> 2 MB)   → 256 KB blocks  (amortise block overhead)
        private const int BlockSizeSmall = 65536;   //  64 KB
        private const int BlockSizeMedium = 65536;   //  64 KB blocks for medium files
        private const int BlockSizeLarge = 262144;   // 256 KB blocks for large files
        private const int FileSizeMedium = 2097152;  //   2 MB threshold

        // Minimum block size before attempting Brotli/Zstd.
        // Both have negligible overhead so 4 KB is fine.
        private const int BrotliMinBlock = 4096;
        private const int ZstdMinBlock = 4096;

        // v3 header flags
        private const uint V3FlagSolidBlocks = 0x00000001;

        // ── Content analysis engine ───────────────────────────────────────────

        // Entropy threshold above which data is almost certainly already
        // compressed or encrypted -- force stored, skip all codec attempts.
        // Set high (7.95) so only truly random data is skipped.
        private const double CompressedEntropyThreshold = 7.95;

        // Magic-byte incompressible check
        private static bool IsIncompressible(byte[] data)
        {
            foreach (var magic in IncompressibleMagic)
            {
                if (data.Length < magic.Length) continue;
                bool match = true;
                for (int i = 0; i < magic.Length && match; i++)
                    if (data[i] != magic[i]) match = false;
                if (match) return true;
            }
            return false;
        }

        // Shannon entropy of a data sample (0-8 bits per byte).
        private static double ComputeEntropy(byte[] data, int sampleLen)
        {
            sampleLen = Math.Min(sampleLen, data.Length);
            if (sampleLen == 0) return 0.0;
            var freq = new int[256];
            for (int i = 0; i < sampleLen; i++) freq[data[i]]++;
            double ent = 0.0;
            for (int i = 0; i < 256; i++)
            {
                if (freq[i] == 0) continue;
                double p = (double)freq[i] / sampleLen;
                ent -= p * Math.Log(p, 2);
            }
            return ent;
        }

        // Compute entropy of the byte-delta sequence of a sample.
        private static double DeltaEntropy8(byte[] data, int sampleLen)
        {
            sampleLen = Math.Min(sampleLen, data.Length);
            if (sampleLen < 2) return 8.0;
            var freq = new int[256];
            for (int i = 1; i < sampleLen; i++)
                freq[(byte)(data[i] - data[i - 1])]++;
            double ent = 0.0;
            int n = sampleLen - 1;
            for (int i = 0; i < 256; i++)
            {
                if (freq[i] == 0) continue;
                double p = (double)freq[i] / n;
                ent -= p * Math.Log(p, 2);
            }
            return ent;
        }

        // Compute entropy of the 16-bit word delta sequence of a sample.
        private static double DeltaEntropy16(byte[] data, int sampleLen)
        {
            sampleLen = Math.Min(sampleLen & ~1, data.Length & ~1);
            if (sampleLen < 4) return 8.0;
            // Measure entropy of low bytes of deltas (good proxy for full 16-bit entropy)
            var freq = new int[256];
            ushort prev = BitConverter.ToUInt16(data, 0);
            int n = 0;
            for (int i = 2; i + 1 < sampleLen; i += 2)
            {
                ushort val = BitConverter.ToUInt16(data, i);
                ushort d = (ushort)(val - prev);
                freq[d & 0xFF]++;
                freq[(d >> 8) & 0xFF]++;
                n += 2;
                prev = val;
            }
            if (n == 0) return 8.0;
            double ent = 0.0;
            for (int i = 0; i < 256; i++)
            {
                if (freq[i] == 0) continue;
                double p = (double)freq[i] / n;
                ent -= p * Math.Log(p, 2);
            }
            return ent;
        }

        // Compute entropy of the 32-bit dword delta sequence of a sample.
        private static double DeltaEntropy32(byte[] data, int sampleLen)
        {
            sampleLen = Math.Min(sampleLen & ~3, data.Length & ~3);
            if (sampleLen < 8) return 8.0;
            var freq = new int[256];
            uint prev = BitConverter.ToUInt32(data, 0);
            int n = 0;
            for (int i = 4; i + 3 < sampleLen; i += 4)
            {
                uint val = BitConverter.ToUInt32(data, i);
                uint d = val - prev;
                freq[d & 0xFF]++;
                freq[(d >> 8) & 0xFF]++;
                freq[(d >> 16) & 0xFF]++;
                freq[(d >> 24) & 0xFF]++;
                n += 4;
                prev = val;
            }
            if (n == 0) return 8.0;
            double ent = 0.0;
            for (int i = 0; i < 256; i++)
            {
                if (freq[i] == 0) continue;
                double p = (double)freq[i] / n;
                ent -= p * Math.Log(p, 2);
            }
            return ent;
        }

        // Compute entropy of stereo interleaved 16-bit delta sequence.
        private static double DeltaEntropyStereo16(byte[] data, int sampleLen)
        {
            sampleLen = Math.Min(sampleLen & ~3, data.Length & ~3);
            if (sampleLen < 8) return 8.0;
            var freq = new int[256];
            ushort prevL = BitConverter.ToUInt16(data, 0);
            ushort prevR = BitConverter.ToUInt16(data, 2);
            int n = 0;
            for (int i = 4; i + 3 < sampleLen; i += 4)
            {
                ushort L = BitConverter.ToUInt16(data, i);
                ushort R = BitConverter.ToUInt16(data, i + 2);
                ushort dL = (ushort)(L - prevL);
                ushort dR = (ushort)(R - prevR);
                freq[dL & 0xFF]++; freq[(dL >> 8) & 0xFF]++;
                freq[dR & 0xFF]++; freq[(dR >> 8) & 0xFF]++;
                n += 4;
                prevL = L; prevR = R;
            }
            if (n == 0) return 8.0;
            double ent = 0.0;
            for (int i = 0; i < 256; i++)
            {
                if (freq[i] == 0) continue;
                double p = (double)freq[i] / n;
                ent -= p * Math.Log(p, 2);
            }
            return ent;
        }

        // Detect WAV bit depth from fmt chunk.
        private static (int audioFormat, int bitsPerSample, int channels) DetectWavFormat(byte[] data)
        {
            if (data.Length < 44) return (0, 0, 0);
            if (data[0] != 0x52 || data[1] != 0x49 || data[2] != 0x46 || data[3] != 0x46) return (0, 0, 0);
            if (data[8] != 0x57 || data[9] != 0x41 || data[10] != 0x56 || data[11] != 0x45) return (0, 0, 0);
            if (data[12] != 0x66 || data[13] != 0x6D || data[14] != 0x74 || data[15] != 0x20) return (0, 0, 0);
            int audioFormat = data[20] | (data[21] << 8);
            int channels = data[22] | (data[23] << 8);
            int bitsPerSample = data[34] | (data[35] << 8);
            return (audioFormat, bitsPerSample, channels);
        }

        // Detect BMP bit depth.
        private static int DetectBmpBitDepth(byte[] data)
        {
            if (data.Length < 30) return 0;
            if (data[0] != 0x42 || data[1] != 0x4D) return 0;
            return data[28] | (data[29] << 8);
        }

        // Full content analysis -- returns best filter, force-store flag, and entropy diagnostics.
        private static (byte filterFlag, bool forceStore, double rawEnt, double bestDeltaEnt) AnalyseContent(
            byte[] data, string ext)
        {
            if (data.Length == 0) return (V3FilterNone, false, 0, 0);

            // Magic check: known compressed formats -- force stored immediately,
            // no point wasting time trying to compress them.
            if (IsIncompressible(data)) return (V3FilterNone, true, 0, 0);

            // Known extension overrides -- high confidence, apply directly.
            // BestV3Block will still verify the filter actually helps compression.
            if (X86Exts.Contains(ext)) return (V3FilterX86, false, 0, 0);

            if (ext.Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                var (audioFmt, bits, channels) = DetectWavFormat(data);
                // ADPCM and extensible WAV contain already-compressed sample data -- store as-is.
                if (AdpcmWavFormats.Contains(audioFmt)) return (V3FilterNone, true, 0, 0);
                // PCM: pick the best delta filter for the sample width.
                if (bits == 16 && channels >= 2) return (V3FilterDeltaStereo, false, 0, 0);
                if (bits == 16) return (V3FilterDelta16, false, 0, 0);
                if (bits == 8) return (V3FilterDelta8, false, 0, 0);
            }

            if (ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
            {
                int bits = DetectBmpBitDepth(data);
                if (bits == 32) return (V3FilterDelta32, false, 0, 0);
                if (bits == 16) return (V3FilterDelta16, false, 0, 0);
                if (bits == 8) return (V3FilterDelta8, false, 0, 0);
            }

            // Everything else: no pre-filter, let BestV3Block try all codecs
            // (plain deflate + strided deflate at stride 2/4/8 + stored).
            // The compression comparison in BestV3Block is the gate -- not entropy.
            // If nothing beats stored, it stores. No wasted CPU on analysis.
            return (V3FilterNone, false, 0, 0);
        }

        // Legacy shim
        private static byte SelectV3Filter(string ext, byte[] data)
        {
            var (flag, _, _, _) = AnalyseContent(data, ext);
            return flag;
        }

        // ── V3 pre-filters ────────────────────────────────────────────────────

        // Apply pre-filter to whole file before blocking.
        private static byte[] ApplyV3Filter(byte filterFlag, byte[] data)
        {
            if (filterFlag == V3FilterNone) return data;
            var out_ = new byte[data.Length];
            switch (filterFlag)
            {
                case V3FilterX86:
                    Array.Copy(data, out_, data.Length);
                    for (int i = 0; i + 4 < data.Length; i++)
                    {
                        if (data[i] != 0xE8 && data[i] != 0xE9) continue;
                        uint rel = BitConverter.ToUInt32(data, i + 1);
                        uint abs = rel + (uint)(i + 5);
                        out_[i + 1] = (byte)(abs);
                        out_[i + 2] = (byte)(abs >> 8);
                        out_[i + 3] = (byte)(abs >> 16);
                        out_[i + 4] = (byte)(abs >> 24);
                        i += 4;
                    }
                    return out_;

                case V3FilterDelta8:
                    out_[0] = data[0];
                    for (int i = 1; i < data.Length; i++)
                        out_[i] = (byte)(data[i] - data[i - 1]);
                    return out_;

                case V3FilterDelta16:
                    {
                        Array.Copy(data, out_, data.Length);
                        ushort prev = 0;
                        for (int i = 0; i + 1 < data.Length; i += 2)
                        {
                            ushort val = BitConverter.ToUInt16(data, i);
                            ushort delta = (ushort)(val - prev);
                            out_[i] = (byte)(delta);
                            out_[i + 1] = (byte)(delta >> 8);
                            prev = val;
                        }
                        return out_;
                    }

                case V3FilterDeltaStereo:
                    {
                        Array.Copy(data, out_, data.Length);
                        ushort prevL = 0, prevR = 0;
                        for (int i = 0; i + 3 < data.Length; i += 4)
                        {
                            ushort L = BitConverter.ToUInt16(data, i);
                            ushort R = BitConverter.ToUInt16(data, i + 2);
                            ushort dL = (ushort)(L - prevL);
                            ushort dR = (ushort)(R - prevR);
                            out_[i] = (byte)(dL); out_[i + 1] = (byte)(dL >> 8);
                            out_[i + 2] = (byte)(dR); out_[i + 3] = (byte)(dR >> 8);
                            prevL = L; prevR = R;
                        }
                        return out_;
                    }

                case V3FilterDelta32:
                    {
                        Array.Copy(data, out_, data.Length);
                        uint prev = 0;
                        for (int i = 0; i + 3 < data.Length; i += 4)
                        {
                            uint val = BitConverter.ToUInt32(data, i);
                            uint delta = val - prev;
                            out_[i] = (byte)(delta);
                            out_[i + 1] = (byte)(delta >> 8);
                            out_[i + 2] = (byte)(delta >> 16);
                            out_[i + 3] = (byte)(delta >> 24);
                            prev = val;
                        }
                        return out_;
                    }

                default:
                    return data;
            }
        }

        // Reverse pre-filter after block reassembly.
        private static byte[] ReverseV3Filter(byte filterFlag, byte[] data)
        {
            if (filterFlag == V3FilterNone) return data;
            var out_ = new byte[data.Length];
            switch (filterFlag)
            {
                case V3FilterX86:
                    Array.Copy(data, out_, data.Length);
                    for (int i = 0; i + 4 < data.Length; i++)
                    {
                        if (data[i] != 0xE8 && data[i] != 0xE9) continue;
                        uint abs = BitConverter.ToUInt32(data, i + 1);
                        uint rel = abs - (uint)(i + 5);
                        out_[i + 1] = (byte)(rel);
                        out_[i + 2] = (byte)(rel >> 8);
                        out_[i + 3] = (byte)(rel >> 16);
                        out_[i + 4] = (byte)(rel >> 24);
                        i += 4;
                    }
                    return out_;

                case V3FilterDelta8:
                    out_[0] = data[0];
                    for (int i = 1; i < data.Length; i++)
                        out_[i] = (byte)(data[i] + out_[i - 1]);
                    return out_;

                case V3FilterDelta16:
                    {
                        Array.Copy(data, out_, data.Length);
                        ushort prev = 0;
                        for (int i = 0; i + 1 < data.Length; i += 2)
                        {
                            ushort delta = BitConverter.ToUInt16(data, i);
                            ushort val = (ushort)(delta + prev);
                            out_[i] = (byte)(val);
                            out_[i + 1] = (byte)(val >> 8);
                            prev = val;
                        }
                        return out_;
                    }

                case V3FilterDeltaStereo:
                    {
                        Array.Copy(data, out_, data.Length);
                        ushort prevL = 0, prevR = 0;
                        for (int i = 0; i + 3 < data.Length; i += 4)
                        {
                            ushort dL = BitConverter.ToUInt16(data, i);
                            ushort dR = BitConverter.ToUInt16(data, i + 2);
                            ushort L = (ushort)(dL + prevL);
                            ushort R = (ushort)(dR + prevR);
                            out_[i] = (byte)(L); out_[i + 1] = (byte)(L >> 8);
                            out_[i + 2] = (byte)(R); out_[i + 3] = (byte)(R >> 8);
                            prevL = L; prevR = R;
                        }
                        return out_;
                    }

                case V3FilterDelta32:
                    {
                        Array.Copy(data, out_, data.Length);
                        uint prev = 0;
                        for (int i = 0; i + 3 < data.Length; i += 4)
                        {
                            uint delta = BitConverter.ToUInt32(data, i);
                            uint val = delta + prev;
                            out_[i] = (byte)(val);
                            out_[i + 1] = (byte)(val >> 8);
                            out_[i + 2] = (byte)(val >> 16);
                            out_[i + 3] = (byte)(val >> 24);
                            prev = val;
                        }
                        return out_;
                    }

                default:
                    return data;
            }
        }

        // Apply a delta pre-filter to a single block in-place for block-level trials.
        // These are the same transforms as the file-level filters but applied per-block.
        private static byte[] ApplyBlockFilter(byte[] block, byte filterFlag)
        {
            switch (filterFlag)
            {
                case V3FilterDelta8:
                    {
                        var out_ = new byte[block.Length];
                        out_[0] = block[0];
                        for (int i = 1; i < block.Length; i++)
                            out_[i] = (byte)(block[i] - block[i - 1]);
                        return out_;
                    }
                case V3FilterDelta16:
                    {
                        var out_ = new byte[block.Length];
                        Array.Copy(block, out_, block.Length);
                        ushort prev = 0;
                        for (int i = 0; i + 1 < block.Length; i += 2)
                        {
                            ushort val = BitConverter.ToUInt16(block, i);
                            ushort delta = (ushort)(val - prev);
                            out_[i] = (byte)(delta);
                            out_[i + 1] = (byte)(delta >> 8);
                            prev = val;
                        }
                        return out_;
                    }
                case V3FilterDelta32:
                    {
                        var out_ = new byte[block.Length];
                        Array.Copy(block, out_, block.Length);
                        uint prev = 0;
                        for (int i = 0; i + 3 < block.Length; i += 4)
                        {
                            uint val = BitConverter.ToUInt32(block, i);
                            uint delta = val - prev;
                            out_[i] = (byte)(delta);
                            out_[i + 1] = (byte)(delta >> 8);
                            out_[i + 2] = (byte)(delta >> 16);
                            out_[i + 3] = (byte)(delta >> 24);
                            prev = val;
                        }
                        return out_;
                    }
                default:
                    return block;
            }
        }

        // ── Deflate codec ─────────────────────────────────────────────────────

        // Compress a block with raw Deflate. Returns null if not beneficial.
        private static byte[]? CompressDeflate(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var ds = new System.IO.Compression.DeflateStream(
                ms, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
                ds.Write(data, 0, data.Length);
            var result = ms.ToArray();
            return result.Length < data.Length * CompLimit ? result : null;
        }

        // Decompress a raw Deflate block.
        private static byte[] DecompressDeflate(byte[] data, int uncompSize)
        {
            using var ms = new MemoryStream(data);
            using var ds = new System.IO.Compression.DeflateStream(
                ms, System.IO.Compression.CompressionMode.Decompress);
            var out_ = new byte[uncompSize];
            int read = 0;
            while (read < uncompSize)
            {
                int n = ds.Read(out_, read, uncompSize - read);
                if (n == 0) break;
                read += n;
            }
            return out_;
        }

        // ── Stride-split transform ────────────────────────────────────────────
        // Reorders bytes by position within a repeating stride.
        // e.g. stride=4: [A0 A1 A2 A3 B0 B1 B2 B3] -> [A0 B0 | A1 B1 | A2 B2 | A3 B3]
        // Structured numeric data (floats, vectors, matrices) compresses much
        // better after splitting because each plane has lower entropy.

        private static byte[] StrideSplit(byte[] data, int stride)
        {
            var out_ = new byte[data.Length];
            int aligned = (data.Length / stride) * stride;
            int planesz = aligned / stride;
            for (int i = 0; i < aligned; i++)
                out_[(i % stride) * planesz + (i / stride)] = data[i];
            // Copy unaligned tail unmodified
            for (int i = aligned; i < data.Length; i++)
                out_[i] = data[i];
            return out_;
        }

        private static byte[] StrideUnsplit(byte[] data, int stride)
        {
            var out_ = new byte[data.Length];
            int aligned = (data.Length / stride) * stride;
            int planesz = aligned / stride;
            for (int i = 0; i < aligned; i++)
                out_[i] = data[(i % stride) * planesz + (i / stride)];
            for (int i = aligned; i < data.Length; i++)
                out_[i] = data[i];
            return out_;
        }

        // Try compressing stride-split block. Returns [stride(1)] + deflate payload,
        // or null if result is not better than plainSize or stored threshold.
        private static byte[]? CompressStrided(byte[] block, int stride, int plainSize)
        {
            if (block.Length < stride * 4) return null;
            var split = StrideSplit(block, stride);
            using var ms = new MemoryStream();
            using (var ds = new System.IO.Compression.DeflateStream(
                ms, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
                ds.Write(split, 0, split.Length);
            var compressed = ms.ToArray();
            int totalSize = 1 + compressed.Length;   // 1 byte stride header
            if (totalSize >= plainSize) return null;
            if (totalSize >= block.Length * CompLimit) return null;
            var payload = new byte[totalSize];
            payload[0] = (byte)stride;
            Array.Copy(compressed, 0, payload, 1, compressed.Length);
            return payload;
        }

        // Decompress strided block. data = [stride(1)] + raw deflate bytes.
        private static byte[] DecompressStrided(byte[] data, int uncompSize)
        {
            int stride = data[0];
            var deflateData = new byte[data.Length - 1];
            Array.Copy(data, 1, deflateData, 0, deflateData.Length);
            var split = DecompressDeflate(deflateData, uncompSize);
            return StrideUnsplit(split, stride);
        }

        // Estimate compressed size by sampling up to the first 256 KB of data.
        // Fast enough to call twice (with/without filter) for filter selection.
        private static long EstimateCompressedSize(byte[] data)
        {
            int sampleLen = Math.Min(data.Length, V3BlockSize);
            using var ms = new MemoryStream();
            using (var ds = new System.IO.Compression.DeflateStream(
                ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                ds.Write(data, 0, sampleLen);
            // Scale estimate to full file length
            double ratio = sampleLen > 0 ? (double)ms.Length / sampleLen : 1.0;
            return (long)(data.Length * ratio);
        }

        // ── Best-of V3 block compression ──────────────────────────────────────
        //
        // BestV3BlockFast: Deflate + strided only. Safe to call from parallel
        // threads — no cabinet.dll involvement.
        //
        // BestV3Block: full contest including LZX. Called sequentially only.

        private static (byte[] data, byte flag) BestV3BlockFast(byte[] block)
        {
            if (block.Length <= StoreMin) return (block, V3BlkStored);

            var deflate = CompressDeflate(block);
            int bestSize = deflate != null ? deflate.Length : block.Length;
            byte[] bestData = deflate ?? block;
            byte bestFlag = deflate != null ? V3BlkDeflate : V3BlkStored;

            foreach (int stride in new[] { 4, 2, 8 })
            {
                var s = CompressStrided(block, stride, bestSize);
                if (s != null && s.Length < bestSize)
                {
                    bestSize = s.Length;
                    bestData = s;
                    bestFlag = V3BlkStrided;
                }
            }

            return (bestData, bestFlag);
        }

        private static (byte[] data, byte flag) BestV3Block(byte[] block)
        {
            if (block.Length <= StoreMin) return (block, V3BlkStored);

            // Plain deflate baseline
            var deflate = CompressDeflate(block);
            int bestSize = deflate != null ? deflate.Length : block.Length;
            byte[] bestData = deflate ?? block;
            byte bestFlag = deflate != null ? V3BlkDeflate : V3BlkStored;

            // Strided deflate: stride 4 (float/int), 2 (short), 8 (double/matrix)
            foreach (int stride in new[] { 4, 2, 8 })
            {
                var s = CompressStrided(block, stride, bestSize);
                if (s != null && s.Length < bestSize)
                {
                    bestSize = s.Length;
                    bestData = s;
                    bestFlag = V3BlkStrided;
                }
            }

            // Brotli: independent contestant, best ratio on general compressible data.
            // Skip on small blocks where FCI overhead exceeds any gain.
            if (block.Length >= BrotliMinBlock)
            {
                var brotli = XbaBrotli.CompressBrotli(block);
                if (brotli != null && brotli.Length < bestSize)
                {
                    bestSize = brotli.Length;
                    bestData = brotli;
                    bestFlag = V3BlkBrotli;
                }
            }

            return (bestData, bestFlag);
        }

        // ── V3 file collection ────────────────────────────────────────────────

        // Collect files only (no directories) for V3.
        private static List<(string rel, string abs)> CollectFilesOnly(string srcDir)
        {
            var result = new List<(string, string)>();
            RecurseFilesOnly(srcDir, srcDir, result);
            return result;
        }

        private static void RecurseFilesOnly(string root, string dir,
            List<(string, string)> result)
        {
            var dirs = Directory.GetDirectories(dir);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs)
                RecurseFilesOnly(root, d, result);
            var files = Directory.GetFiles(dir);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                var rel = System.IO.Path.GetRelativePath(root, f).Replace('/', '\\');
                result.Add((rel, f));
            }
        }

        private static uint Crc32Path(string path)
        {
            var bytes = Encoding.ASCII.GetBytes(path);
            return Crc32(bytes);
        }

        // ── Variable block size selector ──────────────────────────────────────
        private static int PickBlockSize(int fileSize)
        {
            if (fileSize > FileSizeMedium) return BlockSizeLarge;
            return BlockSizeMedium;  // also covers small files as a single block
        }

        // ── Pack (v3) ─────────────────────────────────────────────────────────

        public static void PackV3(
            string srcDir, string outPath,
            IProgress<ProgressReport>? progress = null,
            IProgress<LogEntry>? log = null,
            CancellationToken ct = default)
        {
            var entries = CollectFilesOnly(srcDir);
            int total = entries.Count;

            var tocOffsets = new uint[total];
            var tocPathCrcs = new uint[total];

            using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write,
                                          FileShare.None, 65536);
            using var bw = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: false);

            // Write placeholder header — toc_offset patched at end.
            bw.Write(MagicV3);       // [4] magic
            bw.Write((uint)total);   // [4] entry_count
            bw.Write((uint)0);       // [4] toc_offset placeholder
            bw.Write((uint)0);       // [4] flags

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (rel, absPath) = entries[i];
                if (rel.Length > 255)
                    throw new InvalidOperationException($"Path exceeds 255-character limit: {rel}");

                var raw = File.ReadAllBytes(absPath);
                uint crc = Crc32(raw);
                string ext = System.IO.Path.GetExtension(rel);

                tocOffsets[i] = (uint)fs.Position;
                tocPathCrcs[i] = Crc32Path(rel);

                var (filterFlag, forceStore, rawEntropy, bestDeltaEnt) = AnalyseContent(raw, ext);
                byte[] src_ = raw;

                if (!forceStore && filterFlag != V3FilterNone)
                {
                    var filtered = ApplyV3Filter(filterFlag, raw);
                    long sizeWith = EstimateCompressedSize(filtered);
                    long sizeWithout = EstimateCompressedSize(raw);
                    if (sizeWith < sizeWithout)
                        src_ = filtered;
                    else
                        filterFlag = V3FilterNone;
                }

                // Variable block size: small/medium files use 64 KB blocks for
                // full codec window coverage; large files use 256 KB blocks.
                int blockSize = PickBlockSize(src_.Length);
                int numBlocks = Math.Max(1, (src_.Length + blockSize - 1) / blockSize);
                var blocks = new (byte[] data, byte flag)[numBlocks];

                // Compress all blocks for this file in parallel.
                // Each block is independent — safe to parallelise.
                // Uses dedicated LongRunning threads (not the shared ThreadPool)
                // to avoid starvation since we're already inside Task.Run.
                RunParallel(numBlocks, ct, bi =>
                {
                    int off = bi * blockSize;
                    int blen = Math.Min(blockSize, src_.Length - off);
                    var blk = new byte[blen];
                    Array.Copy(src_, off, blk, 0, blen);

                    if (forceStore)
                    {
                        blocks[bi] = (blk, V3BlkStored);
                        return;
                    }

                    // Full codec contest — every codec competes independently
                    // on the raw block bytes. Smallest result wins.

                    // Deflate + strided (pure managed, always runs).
                    var (bestData, bestFlag) = BestV3BlockFast(blk);

                    // Brotli: static dictionary advantage on text/mixed content.
                    if (blk.Length >= BrotliMinBlock)
                    {
                        var brotli = XbaBrotli.CompressBrotli(blk);
                        if (brotli != null && brotli.Length < bestData.Length)
                        {
                            bestData = brotli;
                            bestFlag = V3BlkBrotli;
                        }
                    }

                    // Zstd: best general-purpose binary codec, beats Deflate and
                    // Brotli on structured binary data, fast decompression.
                    if (blk.Length >= ZstdMinBlock)
                    {
                        var zstd = XbaZstd.CompressZstd(blk);
                        if (zstd != null && zstd.Length < bestData.Length)
                        {
                            bestData = zstd;
                            bestFlag = V3BlkZstd;
                        }
                    }

                    blocks[bi] = (bestData, bestFlag);
                });

                // Write entry immediately — no RAM accumulation.
                long totalComp = 0;
                int brotliBlocks = 0;
                int zstdBlocks = 0;
                foreach (var (bdata, bflag) in blocks)
                {
                    totalComp += bdata.Length;
                    if (bflag == V3BlkBrotli) brotliBlocks++;
                    if (bflag == V3BlkZstd) zstdBlocks++;
                }

                bw.Write(filterFlag);
                bw.Write((byte)Encoding.ASCII.GetByteCount(rel));
                bw.Write(Encoding.ASCII.GetBytes(rel));
                bw.Write((uint)raw.Length);
                bw.Write(crc);
                bw.Write((ushort)numBlocks);
                bw.Write((uint)blockSize);    // block output size for non-last blocks

                foreach (var (bdata, bflag) in blocks)
                {
                    bw.Write(bflag);
                    bw.Write((uint)bdata.Length);
                    bw.Write(bdata);
                }



                log?.Report(new LogEntry
                {
                    Kind = "file",
                    FilePath = rel,
                    UncompSize = raw.Length,
                    CompSize = totalComp,
                    IsV2 = false,
                    RawEntropy = rawEntropy,
                    BestDeltaEntropy = bestDeltaEnt,
                    SelectedFilter = filterFlag,
                    ForcedStore = forceStore,
                    BrotliBlocks = brotliBlocks,
                    ZstdBlocks = zstdBlocks
                });
                progress?.Report(new ProgressReport { Done = i + 1, Total = total, CurrentFile = rel });
            }

            // Write TOC
            uint tocPosition = (uint)fs.Position;
            bw.Write((uint)total);
            bw.Write((uint)0);       // reserved
            for (int i = 0; i < total; i++)
            {
                bw.Write(tocOffsets[i]);
                bw.Write(tocPathCrcs[i]);
            }

            // Patch toc_offset in header
            fs.Seek(8, SeekOrigin.Begin);
            bw.Write(tocPosition);
        }

        // ── Unpack (v3) ───────────────────────────────────────────────────────

        internal static void UnpackV3(
            BinaryReader br, string destDir,
            IProgress<ProgressReport>? progress,
            IProgress<LogEntry>? log,
            CancellationToken ct)
        {
            int ec = br.ReadInt32();   // entry_count
            uint tocOffset = br.ReadUInt32(); // toc_offset (not needed for sequential read)
            uint flags = br.ReadUInt32();  // archive flags (ignored by extractor)
            _ = tocOffset; _ = flags;

            for (int i = 0; i < ec; i++)
            {
                ct.ThrowIfCancellationRequested();

                byte filterFlag = br.ReadByte();
                int pathLen = br.ReadByte();
                string rel = Encoding.ASCII.GetString(br.ReadBytes(pathLen));

                // Solid entries are not supported -- skip block data and continue
                if (filterFlag == V3FilterSolid)
                {
                    uint uncompSize2 = br.ReadUInt32();
                    br.ReadUInt32(); // crc
                    ushort bc2 = br.ReadUInt16();
                    br.ReadUInt32(); // blockSize (skip)
                    for (int bi = 0; bi < bc2; bi++)
                    {
                        br.ReadByte();
                        uint cs = br.ReadUInt32();
                        br.ReadBytes((int)cs);
                    }
                    _ = uncompSize2;
                    log?.Report(new LogEntry { Kind = "skip", FilePath = rel });
                    continue;
                }

                uint uncompSize = br.ReadUInt32();
                uint storedCrc = br.ReadUInt32();
                ushort blockCount = br.ReadUInt16();
                int blockSize = (int)br.ReadUInt32(); // stored block output size

                // Read and decompress all blocks
                var assembled = new byte[uncompSize];
                int writePos = 0;

                for (int bi = 0; bi < blockCount; bi++)
                {
                    byte bflag = br.ReadByte();
                    uint compSize = br.ReadUInt32();
                    byte[] bdata = br.ReadBytes((int)compSize);

                    // blockOut: last block gets remaining bytes, others use stored blockSize.
                    int blockOut = bi == blockCount - 1
                        ? (int)((uint)uncompSize - (uint)writePos)
                        : blockSize;

                    byte[] decoded = bflag switch
                    {
                        V3BlkStored => bdata,
                        V3BlkLz77 => DecompressLz77(bdata, blockOut),
                        V3BlkLzss => DecompressLzss(bdata, blockOut),
                        V3BlkDeflate => DecompressDeflate(bdata, blockOut),
                        V3BlkStrided => DecompressStrided(bdata, blockOut),
                        V3BlkBrotli => XbaBrotli.DecompressBrotli(bdata, blockOut),
                        V3BlkZstd => XbaZstd.DecompressZstd(bdata, blockOut),
                        _ => throw new InvalidDataException($"Unknown V3 block flag 0x{bflag:X2} in {rel}")
                    };

                    Array.Copy(decoded, 0, assembled, writePos, decoded.Length);
                    writePos += decoded.Length;
                }

                // Reverse pre-filter
                if (filterFlag != V3FilterNone)
                    assembled = ReverseV3Filter(filterFlag, assembled);

                // CRC verify
                uint actualCrc = Crc32(assembled);
                if (actualCrc != storedCrc)
                {
                    log?.Report(new LogEntry
                    {
                        Kind = "error",
                        FilePath = rel,
                        ExpectedCrc = storedCrc,
                        ActualCrc = actualCrc
                    });
                }

                // Write output file
                string outPath = System.IO.Path.Combine(destDir,
                    rel.Replace('\\', System.IO.Path.DirectorySeparatorChar));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outPath)!);
                File.WriteAllBytes(outPath, assembled);

                log?.Report(new LogEntry
                {
                    Kind = "file",
                    FilePath = rel,
                    UncompSize = assembled.Length,
                    ExpectedCrc = storedCrc,
                    ActualCrc = actualCrc
                });
                progress?.Report(new ProgressReport { Done = i + 1, Total = ec, CurrentFile = rel });
            }
        }

        // ── List (v3) ─────────────────────────────────────────────────────────

        internal static List<ArchiveEntry> ListV3(BinaryReader br)
        {
            var result = new List<ArchiveEntry>();
            int ec = br.ReadInt32();
            uint tocOffset = br.ReadUInt32();
            uint flags = br.ReadUInt32();
            _ = tocOffset; _ = flags;

            for (int i = 0; i < ec; i++)
            {
                byte filterFlag = br.ReadByte();
                int pathLen = br.ReadByte();
                string rel = Encoding.ASCII.GetString(br.ReadBytes(pathLen));

                if (filterFlag == V3FilterSolid)
                {
                    uint usz2 = br.ReadUInt32();
                    br.ReadUInt32();
                    ushort bc2 = br.ReadUInt16();
                    br.ReadUInt32(); // blockSize (skip)
                    long totalComp2 = 0;
                    for (int bi = 0; bi < bc2; bi++)
                    {
                        br.ReadByte();
                        uint cs = br.ReadUInt32();
                        totalComp2 += cs;
                        br.ReadBytes((int)cs);
                    }
                    result.Add(new ArchiveEntry
                    {
                        Type = EntryType.File,
                        Path = rel,
                        UncompSize = usz2,
                        CompSize = totalComp2,
                        IsV3 = true,
                        V3FilterFlag = filterFlag,
                        BlockCount = bc2
                    });
                    continue;
                }

                uint uncomp = br.ReadUInt32();
                uint crc = br.ReadUInt32();
                ushort blockCount = br.ReadUInt16();
                br.ReadUInt32(); // blockSize (not needed for listing)
                long totalComp = 0;

                for (int bi = 0; bi < blockCount; bi++)
                {
                    br.ReadByte();
                    uint cs = br.ReadUInt32();
                    totalComp += cs;
                    br.ReadBytes((int)cs);
                }

                result.Add(new ArchiveEntry
                {
                    Type = EntryType.File,
                    Path = rel,
                    UncompSize = uncomp,
                    CompSize = totalComp,
                    Crc32 = crc,
                    BlockCount = blockCount,
                    IsV3 = true,
                    V3FilterFlag = filterFlag
                });
            }
            return result;
        }

        // ── Test (v3) ─────────────────────────────────────────────────────────

        internal static TestResult TestV3(
            BinaryReader br,
            IProgress<ProgressReport>? progress,
            CancellationToken ct)
        {
            int ok = 0, errors = 0;
            int ec = br.ReadInt32();
            uint tocOffset = br.ReadUInt32();
            uint flags = br.ReadUInt32();
            _ = tocOffset; _ = flags;

            for (int i = 0; i < ec; i++)
            {
                ct.ThrowIfCancellationRequested();

                byte filterFlag = br.ReadByte();
                int pathLen = br.ReadByte();
                string rel = Encoding.ASCII.GetString(br.ReadBytes(pathLen));

                if (filterFlag == V3FilterSolid)
                {
                    br.ReadUInt32(); // uncomp_size
                    br.ReadUInt32(); // crc
                    ushort bc2 = br.ReadUInt16();
                    br.ReadUInt32(); // blockSize (skip)
                    for (int bi = 0; bi < bc2; bi++)
                    {
                        br.ReadByte();
                        uint cs = br.ReadUInt32();
                        br.ReadBytes((int)cs);
                    }
                    continue;
                }

                uint uncompSize = br.ReadUInt32();
                uint storedCrc = br.ReadUInt32();
                ushort blockCount = br.ReadUInt16();
                int testBlockSize = (int)br.ReadUInt32();

                var assembled = new byte[uncompSize];
                int writePos = 0;
                bool failed = false;

                for (int bi = 0; bi < blockCount && !failed; bi++)
                {
                    byte bflag = br.ReadByte();
                    uint compSize = br.ReadUInt32();
                    byte[] bdata = br.ReadBytes((int)compSize);

                    int blockOut = bi == blockCount - 1
                        ? (int)((uint)uncompSize - (uint)writePos)
                        : testBlockSize;

                    try
                    {
                        byte[] decoded = bflag switch
                        {
                            V3BlkStored => bdata,
                            V3BlkLz77 => DecompressLz77(bdata, blockOut),
                            V3BlkLzss => DecompressLzss(bdata, blockOut),
                            V3BlkDeflate => DecompressDeflate(bdata, blockOut),
                            V3BlkStrided => DecompressStrided(bdata, blockOut),
                            V3BlkBrotli => XbaBrotli.DecompressBrotli(bdata, blockOut),
                            V3BlkZstd => XbaZstd.DecompressZstd(bdata, blockOut),
                            _ => throw new InvalidDataException($"Unknown block flag 0x{bflag:X2}")
                        };
                        Array.Copy(decoded, 0, assembled, writePos, decoded.Length);
                        writePos += decoded.Length;
                    }
                    catch { failed = true; }
                }

                if (!failed)
                {
                    if (filterFlag != V3FilterNone)
                        assembled = ReverseV3Filter(filterFlag, assembled);

                    uint actualCrc = Crc32(assembled);
                    if (actualCrc == storedCrc) ok++; else errors++;
                }
                else
                {
                    errors++;
                }

                progress?.Report(new ProgressReport { Done = i + 1, Total = ec, CurrentFile = rel });
            }

            return new TestResult { Ok = ok, Errors = errors };
        }
    }
}
// XbaCore.cs — XBA archive format engine  v3
//
// ============================================================================
// XBA v1 format  (magic XBA\x01)
// ============================================================================
//
//   Header:    magic[4]  entry_count[4 LE]
//   Per entry: flags[1]  path_len[1]  path[N]
//              if file:  uncomp_size[4 LE]  comp_size[4 LE]  crc32[4 LE]
//                        data[comp_size]
//
//   file flags (v1):
//     0x01 = directory
//     0x00 = file, LZ77 or stored
//     0x02 = file, x86-filtered + LZ77 or stored
//     0x03 = file, LZSS or stored
//     0x04 = file, x86-filtered + LZSS or stored
//
//   comp_size == uncomp_size => stored.
//   CRC32 is of the original unfiltered data.
//
// ============================================================================
// XBA v2 format  (magic XBA\x02)
// ============================================================================
//
//   Header:    magic[4]  entry_count[4 LE]
//   Per entry: file_flag[1]  path_len[1]  path[N]
//              if file:  uncomp_size[4 LE]  crc32[4 LE]
//                        block_count[2 LE]
//                        blocks[block_count]:
//                          block_flag[1]  comp_size[4 LE]  data[comp_size]
//
//   file_flag (v2):
//     0x01 = directory
//     0x00 = file, no x86 filter
//     0x02 = file, x86 filter applied to whole file before blocking
//
//   block_flag (v2):
//     0x00 = stored
//     0x01 = LZ77
//     0x02 = LZSS
//     0x03 = RLE
//     0x04 = LZ77 + Huffman
//     0x05 = LZSS + Huffman
//
//   Each block decompresses to BlockSize (65536) bytes except the last.
//   CRC32 is of the final decoded + unfiltered data.
//   x86 unfilter is applied to the whole assembled file, not per-block.
//
//   Huffman block layout:
//     code_lengths[256]  one byte per symbol; 0 = absent
//     bitstream[...]     remainder, packed LSB-first
//
// ============================================================================
// XBA v3 format  (magic XBA\x03)
// ============================================================================
//
//   Header (16 bytes):
//     magic[4]         "XBA\x03"
//     entry_count[4]   uint32 LE  — file entries only (no dir entries)
//     toc_offset[4]    uint32 LE  — byte offset of TOC from file start
//     flags[4]         uint32 LE  — bit 0 = has_solid_blocks
//
//   Per entry (files only):
//     filter_flag[1]   pre-filter (0x00=none 0x01=x86 0x02=delta8 0x03=delta16
//                                  0x04=deltastereo16 0x05=delta32 0xFF=solid)
//     path_len[1]      uint8
//     path[N]          ASCII, backslash-separated, relative
//     uncomp_size[4]   uint32 LE
//     crc32[4]         uint32 LE  — CRC32 of final decoded+unfiltered data
//     block_count[2]   uint16 LE
//     blocks[]:
//       block_flag[1]  0x00=stored 0x01=lz77 0x02=lzss 0x03=deflate 0x04=strided
//                    strided payload: [stride(1)] + raw deflate of stride-split block
//       comp_size[4]   uint32 LE
//       data[comp_size]
//
//   TOC at toc_offset:
//     entry_count[4]   uint32 LE  — must match header
//     reserved[4]      uint32 LE  — write zero
//     per entry [8 bytes]:
//       data_offset[4] uint32 LE  — byte offset of entry from archive start
//       path_crc[4]    uint32 LE  — CRC32 of path string
//
//   Pre-filters applied whole-file before blocking; reversed after reassembly.
//   LZ ring buffer carries forward between blocks of same file (not reset).
//   Deflate blocks use raw DEFLATE (RFC 1951), no zlib header/trailer.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XbaTool
{
    public enum EntryType { Directory, File }

    public class ArchiveEntry
    {
        public EntryType Type { get; init; }
        public string Path { get; init; } = "";
        public long UncompSize { get; init; }
        public long CompSize { get; init; }   // sum of all block comp sizes (v2), or raw comp_size (v1)
        public uint Crc32 { get; init; }
        public bool X86Filter { get; init; }
        // v1 fields
        public bool UsesLzss { get; init; }
        // v2 fields
        public int BlockCount { get; init; }
        public bool IsV2 { get; init; }
        // v3 fields
        public bool IsV3 { get; init; }
        public byte V3FilterFlag { get; init; }

        public bool Stored => CompSize == UncompSize;
        public double Ratio => UncompSize > 0
                                     ? (double)CompSize / UncompSize * 100.0
                                     : 100.0;
    }

    public sealed class ProgressReport
    {
        public int Done { get; init; }
        public int Total { get; init; }
        public string CurrentFile { get; init; } = "";
    }

    public sealed class LogEntry
    {
        public string Kind { get; init; } = "";
        public string FilePath { get; init; } = "";
        public long UncompSize { get; init; }
        public long CompSize { get; init; }
        public bool X86 { get; init; }
        public bool Lzss { get; init; }
        public bool IsV2 { get; init; }
        public uint ActualCrc { get; init; }
        public uint ExpectedCrc { get; init; }
        public uint FilteredCrc { get; init; }
        // V3 analysis diagnostics
        public double RawEntropy { get; init; }
        public double BestDeltaEntropy { get; init; }
        public byte SelectedFilter { get; init; }
        public bool ForcedStore { get; init; }
        public int BrotliBlocks { get; init; }   // number of blocks compressed with Brotli
        public int ZstdBlocks { get; init; }   // number of blocks compressed with Zstd
    }

    public sealed class TestResult
    {
        public int Ok { get; init; }
        public int Errors { get; init; }
    }

    public static partial class XbaCodec
    {
        // ── Format constants ─────────────────────────────────────────────────

        // LZ77 parameters
        private const int LzWin = 16384;
        private const int LzWinMask = LzWin - 1;
        private const int LzMin = 3;
        private const int LzMax = 18;

        // LZSS parameters
        private const int LsWin = 32768;
        private const int LsWinMask = LsWin - 1;
        private const int LsMin = 2;
        private const int LsMax = 65;

        // Compression tuning
        private const int StoreMin = 64;
        private const double CompLimit = 0.98;
        private const int MaxChain = 256;
        private const int BlockSize = 65536;

        // Huffman
        private const int HuffSyms = 256;

        // incompressible magic signatures — force stored, skip compression
        private static readonly byte[][] IncompressibleMagic = {
            // General compressed/encoded formats
            new byte[] { 0xFF, 0xD8, 0xFF },                    // JPEG
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A }, // PNG
            new byte[] { 0x4F, 0x67, 0x67, 0x53 },              // OGG
            new byte[] { 0x1F, 0x8B },                          // GZIP
            new byte[] { 0x50, 0x4B, 0x03, 0x04 },              // ZIP
            new byte[] { 0x52, 0x61, 0x72, 0x21 },              // RAR
            new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, // 7-Zip
            // Xbox-specific pre-compressed/pre-encoded containers
            new byte[] { 0x58, 0x4D, 0x56, 0x48 },              // XMV  (Xbox Media Video)
            new byte[] { 0x58, 0x50, 0x52, 0x00 },              // XPR0 (Xbox texture pack v0)
            new byte[] { 0x58, 0x50, 0x52, 0x32 },              // XPR2 (Xbox texture pack v2)
            new byte[] { 0x57, 0x42, 0x4E, 0x44 },              // WBND (XWB wave bank, big-endian)
            new byte[] { 0x44, 0x4E, 0x42, 0x57 },              // DNBW (XWB wave bank, little-endian)
            new byte[] { 0x53, 0x44, 0x42, 0x4B },              // SDBK (XSB sound bank)
        };

        // WAV audioFormat values whose bulk data is already compressed -- force stored.
        // 0x0002 = MS ADPCM, 0x0011 = IMA ADPCM, 0xFFFE = WAVE_FORMAT_EXTENSIBLE
        private static readonly HashSet<int> AdpcmWavFormats =
            new() { 0x0002, 0x0011, 0xFFFE };

        private static readonly HashSet<string> X86Exts =
            new(StringComparer.OrdinalIgnoreCase)
            { ".xbe", ".exe", ".dll", ".sys", ".ocx" };

        // ── CRC-32 ───────────────────────────────────────────────────────────

        private static readonly uint[] CrcTab = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int j = 0; j < 8; j++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }

        public static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data)
                crc = CrcTab[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        public static uint Crc32(byte[] data, int offset, int count)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++)
                crc = CrcTab[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        // ── x86 filter ───────────────────────────────────────────────────────

        public static byte[] X86Filter(byte[] data)
        {
            var buf = (byte[])data.Clone();
            int n = buf.Length;
            for (int i = 0; i < n - 4; i++)
            {
                if (buf[i] != 0xE8 && buf[i] != 0xE9) continue;
                uint rel = (uint)buf[i + 1] | ((uint)buf[i + 2] << 8)
                         | ((uint)buf[i + 3] << 16) | ((uint)buf[i + 4] << 24);
                uint abs = unchecked(rel + (uint)(i + 5));
                buf[i + 1] = (byte)(abs); buf[i + 2] = (byte)(abs >> 8);
                buf[i + 3] = (byte)(abs >> 16); buf[i + 4] = (byte)(abs >> 24);
                i += 4;
            }
            return buf;
        }

        public static byte[] X86Unfilter(byte[] data)
        {
            var buf = (byte[])data.Clone();
            int n = buf.Length;
            for (int i = 0; i < n - 4; i++)
            {
                if (buf[i] != 0xE8 && buf[i] != 0xE9) continue;
                uint abs = (uint)buf[i + 1] | ((uint)buf[i + 2] << 8)
                         | ((uint)buf[i + 3] << 16) | ((uint)buf[i + 4] << 24);
                uint rel = unchecked(abs - (uint)(i + 5));
                buf[i + 1] = (byte)(rel); buf[i + 2] = (byte)(rel >> 8);
                buf[i + 3] = (byte)(rel >> 16); buf[i + 4] = (byte)(rel >> 24);
                i += 4;
            }
            return buf;
        }

        // ── Hash ─────────────────────────────────────────────────────────────

        private static int Hash(byte[] d, int p, int needed)
        {
            if (p + needed > d.Length) return 0;
            uint v = (uint)d[p]
                   | ((uint)d[p + 1] << 8)
                   | ((uint)(p + 2 < d.Length ? d[p + 2] : 0) << 16)
                   | ((uint)(p + 3 < d.Length ? d[p + 3] : 0) << 24);
            v ^= v >> 16; v *= 0x45d9f3b; v ^= v >> 16;
            return (int)(v & 0x7FFFFFFF);
        }

        // ── Generic LZ compressor (LZ77 and LZSS share one implementation) ───

        private static byte[] CompressCore(
            byte[] data, int winSize, int winMask,
            int minMatch, int maxMatch, bool isLzss)
        {
            int n = data.Length;
            if (n == 0) return Array.Empty<byte>();

            var win = new byte[winSize];
            int wpos = 0;
            int hsize = 1 << 15, hmask = hsize - 1;
            var head = new int[hsize];
            var prev = new int[n];
            for (int i = 0; i < hsize; i++) head[i] = -1;
            for (int i = 0; i < n; i++) prev[i] = -1;

            var outBuf = new byte[n + (n >> 3) + 64];
            int outPos = 0;

            void Grow()
            {
                if (outPos + 8 < outBuf.Length) return;
                Array.Resize(ref outBuf, outBuf.Length * 2);
            }

            void Insert(int p)
            {
                if (p + minMatch <= n)
                {
                    int h = Hash(data, p, minMatch) & hmask;
                    prev[p] = head[h]; head[h] = p;
                }
                win[wpos] = data[p];
                wpos = (wpos + 1) & winMask;
            }

            void FindMatch(int pos, out int bestLen, out int bestWoff)
            {
                bestLen = 0; bestWoff = 0;
                if (pos + minMatch > n) return;
                int h = Hash(data, pos, minMatch) & hmask;
                int cp = head[h], checked_ = 0;
                while (cp >= 0 && checked_ < MaxChain)
                {
                    int dist = pos - cp;
                    if (dist <= 0 || dist > winSize) break;
                    int maxMl = Math.Min(maxMatch, Math.Min(n - pos, n - cp));
                    int ml = 0;
                    while (ml < maxMl && data[cp + ml] == data[pos + ml]) ml++;
                    if (ml > bestLen)
                    {
                        bestLen = ml;
                        bestWoff = (pos - dist) & winMask;
                        if (bestLen == maxMatch) break;
                    }
                    cp = prev[cp]; checked_++;
                }
                if (bestLen < minMatch) { bestLen = 0; bestWoff = 0; }
            }

            void EmitMatch(int woff, int len)
            {
                if (isLzss)
                {
                    uint tok = (uint)(woff & 0x7FFF) | ((uint)(len - 2) << 15);
                    outBuf[outPos++] = (byte)(tok);
                    outBuf[outPos++] = (byte)(tok >> 8);
                    outBuf[outPos++] = (byte)(tok >> 16);
                }
                else
                {
                    uint tok = (uint)(woff & 0x3FFF) | ((uint)(len - 3) << 14);
                    outBuf[outPos++] = (byte)(tok);
                    outBuf[outPos++] = (byte)(tok >> 8);
                }
            }

            int pos = 0;
            while (pos < n)
            {
                if (outPos > n) return data;
                Grow();
                int ctrlIdx = outPos++; byte ctrl = 0; int sym = 0;
                while (sym < 8 && pos < n)
                {
                    FindMatch(pos, out int ml0, out int mw0);
                    if (ml0 >= minMatch && pos + 1 < n)
                    {
                        FindMatch(pos + 1, out int ml1, out _);
                        if (ml1 > ml0)
                        {
                            Grow(); outBuf[outPos++] = data[pos];
                            Insert(pos); pos++; sym++;
                            if (sym >= 8 || pos >= n) break;
                            FindMatch(pos, out ml0, out mw0);
                        }
                    }
                    Grow();
                    if (ml0 >= minMatch)
                    {
                        ctrl |= (byte)(1 << sym);
                        EmitMatch(mw0, ml0);
                        for (int k = 0; k < ml0; k++) Insert(pos + k);
                        pos += ml0;
                    }
                    else
                    {
                        outBuf[outPos++] = data[pos];
                        Insert(pos); pos++;
                    }
                    sym++;
                }
                outBuf[ctrlIdx] = ctrl;
            }

            var result = new byte[outPos];
            Array.Copy(outBuf, result, outPos);
            return result;
        }

        public static byte[] CompressLz77(byte[] data) =>
            CompressCore(data, LzWin, LzWinMask, LzMin, LzMax, false);

        public static byte[] CompressLzss(byte[] data) =>
            CompressCore(data, LsWin, LsWinMask, LsMin, LsMax, true);

        // ── RLE compressor ───────────────────────────────────────────────────
        //
        // Control byte per run:
        //   bit 7 clear : repeat run  -- (ctrl+1) copies of next byte
        //   bit 7 set   : literal run -- copy next (ctrl & 0x7F)+1 bytes
        //
        // Max run length 128 per control byte (0x00..0x7F = 1..128).

        public static byte[] CompressRle(byte[] data)
        {
            int n = data.Length;
            if (n == 0) return Array.Empty<byte>();

            var out_ = new byte[n * 2 + 8];  // worst case: every byte is literal
            int opos = 0;

            int i = 0;
            while (i < n)
            {
                // Check for a repeat run
                int runLen = 1;
                while (runLen < 128 && i + runLen < n && data[i + runLen] == data[i])
                    runLen++;

                if (runLen >= 3)
                {
                    // Encode as repeat run
                    out_[opos++] = (byte)(runLen - 1);   // bit7 clear, count-1
                    out_[opos++] = data[i];
                    i += runLen;
                }
                else
                {
                    // Find the longest literal run (no repeats >= 3)
                    int litStart = i;
                    int litLen = 0;
                    while (litLen < 128 && i < n)
                    {
                        int ahead = 1;
                        while (ahead < 3 && i + ahead < n && data[i + ahead] == data[i])
                            ahead++;
                        if (ahead >= 3) break;   // upcoming repeat, stop literal run
                        litLen++;
                        i++;
                    }
                    out_[opos++] = (byte)(0x80 | (litLen - 1));  // bit7 set, count-1
                    Array.Copy(data, litStart, out_, opos, litLen);
                    opos += litLen;
                }
            }

            // If RLE output is no smaller, signal incompressible by returning
            // the original array reference (same pattern as CompressCore)
            if (opos >= n) return data;

            var result = new byte[opos];
            Array.Copy(out_, result, opos);
            return result;
        }

        // ── LZ77 decompressor ────────────────────────────────────────────────

        public static byte[] DecompressLz77(byte[] data, int uncompSize)
        {
            var out_ = new byte[uncompSize];
            var win = new byte[LzWin];
            int wpos = 0, opos = 0, spos = 0, slen = data.Length;
            while (opos < uncompSize && spos < slen)
            {
                byte ctrl = data[spos++];
                for (int bit = 0; bit < 8; bit++)
                {
                    if (opos >= uncompSize || spos >= slen) break;
                    if ((ctrl & (1 << bit)) != 0)
                    {
                        if (spos + 1 >= slen) break;
                        int tok = data[spos++] | (data[spos++] << 8);
                        int offset = tok & 0x3FFF;
                        int length = ((tok >> 14) & 0xF) + LzMin;
                        for (int k = 0; k < length && opos < uncompSize; k++)
                        {
                            byte b = win[offset]; offset = (offset + 1) & LzWinMask;
                            win[wpos] = b; wpos = (wpos + 1) & LzWinMask;
                            out_[opos++] = b;
                        }
                    }
                    else
                    {
                        byte b = data[spos++];
                        win[wpos] = b; wpos = (wpos + 1) & LzWinMask;
                        out_[opos++] = b;
                    }
                }
            }
            return out_;
        }

        // ── LZSS decompressor ────────────────────────────────────────────────

        public static byte[] DecompressLzss(byte[] data, int uncompSize)
        {
            var out_ = new byte[uncompSize];
            var win = new byte[LsWin];
            int wpos = 0, opos = 0, spos = 0, slen = data.Length;
            while (opos < uncompSize && spos < slen)
            {
                byte ctrl = data[spos++];
                for (int bit = 0; bit < 8; bit++)
                {
                    if (opos >= uncompSize || spos >= slen) break;
                    if ((ctrl & (1 << bit)) != 0)
                    {
                        if (spos + 2 >= slen) break;
                        int tok = data[spos++] | (data[spos++] << 8) | (data[spos++] << 16);
                        int offset = tok & 0x7FFF;
                        int length = ((tok >> 15) & 0x3F) + LsMin;
                        for (int k = 0; k < length && opos < uncompSize; k++)
                        {
                            byte b = win[offset]; offset = (offset + 1) & LsWinMask;
                            win[wpos] = b; wpos = (wpos + 1) & LsWinMask;
                            out_[opos++] = b;
                        }
                    }
                    else
                    {
                        byte b = data[spos++];
                        win[wpos] = b; wpos = (wpos + 1) & LsWinMask;
                        out_[opos++] = b;
                    }
                }
            }
            return out_;
        }

        // ── RLE decompressor ─────────────────────────────────────────────────

        public static byte[] DecompressRle(byte[] data, int uncompSize)
        {
            var out_ = new byte[uncompSize];
            int opos = 0, spos = 0, slen = data.Length;
            while (opos < uncompSize && spos < slen)
            {
                byte ctrl = data[spos++];
                int count = (ctrl & 0x7F) + 1;
                if ((ctrl & 0x80) != 0)
                {
                    // Literal run
                    for (int k = 0; k < count && opos < uncompSize && spos < slen; k++)
                        out_[opos++] = data[spos++];
                }
                else
                {
                    // Repeat run
                    if (spos >= slen) break;
                    byte val = data[spos++];
                    for (int k = 0; k < count && opos < uncompSize; k++)
                        out_[opos++] = val;
                }
            }
            return out_;
        }

        // ── Canonical Huffman ────────────────────────────────────────────────

        // Build code-length table from a block of data (for the packer).
        // Returns a 256-byte array; entry[sym] = code length in bits, 0 = absent.
        // ── Canonical Huffman ────────────────────────────────────────────────
        //
        // We use a length-limited canonical Huffman capped at HuffMaxBits=15.
        // (15 bits, not 16, avoids any edge case where a 16-bit code index
        // into a 65536-entry table overflows a signed int after shifting.)
        //
        // Build steps:
        //   1. Count symbol frequencies.
        //   2. Sort symbols by frequency (ascending), ties broken by symbol value.
        //   3. Assign depths using the in-place O(n) algorithm on a sorted list
        //      (Moffat & Turpin 1997) — no explicit tree, no recursion.
        //   4. Cap all depths at HuffMaxBits; verify Kraft sum <= 1.
        //      If violated, fall back to uniform lengths.
        //
        // Encode/decode use standard canonical assignment:
        //   - Sort symbols by (length ASC, symbol ASC).
        //   - First code at each length = (prev_code + 1) << (new_len - prev_len).
        //   - Bitstream is LSB-first packed into bytes.
        //
        // Block layout on disk:
        //   lz_size[4 LE]      byte count of the LZ intermediate
        //   code_lengths[256]  one byte per symbol 0-255; 0 = absent
        //   bitstream[...]     remainder of comp_size bytes

        private const int HuffMaxBits = 15;   // 15 keeps table at 32768 entries, no int overflow

        // Returns a 256-byte code-length array.  Entry [sym] = bits needed; 0 = absent.
        private static byte[] BuildCodeLengths(byte[] data)
        {
            // 1. Count frequencies
            var freq = new long[HuffSyms];
            foreach (byte b in data) freq[b]++;

            // Collect present symbols sorted by (freq ASC, sym ASC)
            var syms = new List<(long freq, int sym)>(HuffSyms);
            for (int i = 0; i < HuffSyms; i++)
                if (freq[i] > 0) syms.Add((freq[i], i));

            if (syms.Count == 0) return new byte[HuffSyms];

            // Single-symbol: length 1
            if (syms.Count == 1)
            {
                var r = new byte[HuffSyms];
                r[syms[0].sym] = 1;
                return r;
            }

            syms.Sort((a, b) => a.freq != b.freq
                ? a.freq.CompareTo(b.freq)
                : a.sym.CompareTo(b.sym));

            int n = syms.Count;

            // 2. Moffat-Turpin in-place depth assignment.
            // We work on a long[] of frequencies; the algorithm overwrites them
            // with depths.  Uses only the sorted frequency array — no tree nodes.
            var A = new long[n];
            for (int i = 0; i < n; i++) A[i] = syms[i].freq;

            // Phase 1: reduce
            int leaf = 0, root = 0;
            long w1, w2;
            for (int next = 1; next < n; next++)
            {
                // Select smallest weight 1
                if (leaf >= n || (root < next && A[root] < A[leaf]))
                { w1 = A[root]; A[root++] = next; }
                else
                { w1 = A[leaf++]; }
                // Select smallest weight 2
                if (leaf >= n || (root < next && A[root] < A[leaf]))
                { w2 = A[root]; A[root++] = next; }
                else
                { w2 = A[leaf++]; }
                A[next] = w1 + w2;
            }

            // Phase 2: compute depths from the chain
            A[n - 2] = 0;
            for (int i = n - 3; i >= 0; i--)
                A[i] = A[(int)A[i]] + 1;

            // Phase 3: count symbols at each depth and assign depths to leaves
            int available = 1, used = 0, depth = 0;
            int next2 = n - 1;
            int root2 = n - 2;
            while (available > 0)
            {
                while (root2 >= 0 && A[root2] == depth)
                { used++; root2--; }
                while (available > used)
                {
                    A[next2--] = depth;
                    available--;
                }
                available = 2 * used;
                depth++;
                used = 0;
            }

            // A[0..n-1] now holds the depth for each symbol in sorted order
            var codeLens = new byte[HuffSyms];
            for (int i = 0; i < n; i++)
            {
                int d = (int)A[i];
                if (d > HuffMaxBits) d = HuffMaxBits;
                codeLens[syms[i].sym] = (byte)d;
            }

            // 3. Verify Kraft inequality — if violated, fall back to uniform lengths
            {
                long kraft = 0;
                long unit = 1L << HuffMaxBits;
                for (int i = 0; i < HuffSyms; i++)
                    if (codeLens[i] > 0) kraft += unit >> codeLens[i];
                if (kraft > unit)
                {
                    int uniformLen = 1;
                    while ((1 << uniformLen) < n) uniformLen++;
                    if (uniformLen > HuffMaxBits) uniformLen = HuffMaxBits;
                    for (int i = 0; i < HuffSyms; i++)
                        if (codeLens[i] > 0) codeLens[i] = (byte)uniformLen;
                }
            }

            return codeLens;
        }

        // Assign canonical codes from a code-length table.
        // Returns int[256] where entry[sym] = canonical code value (0 if absent).
        // Sort present symbols by (len ASC, sym ASC); first code = 0, then
        // next = (prev + 1) << (newLen - prevLen) on each length increase.
        private static int[] AssignCanonicalCodes(byte[] codeLens)
        {
            var pairs = new List<(int len, int sym)>(HuffSyms);
            for (int i = 0; i < HuffSyms; i++)
                if (codeLens[i] > 0) pairs.Add((codeLens[i], i));
            pairs.Sort((a, b) => a.len != b.len ? a.len.CompareTo(b.len) : a.sym.CompareTo(b.sym));

            var codes = new int[HuffSyms];
            int code = 0;
            int prevLen = 0;
            foreach (var (len, sym) in pairs)
            {
                if (prevLen == 0)
                    code = 0;
                else
                    code = (code + 1) << (len - prevLen);
                codes[sym] = code;
                prevLen = len;
            }
            return codes;
        }

        // Compress data[] using canonical Huffman as a second stage over LZ output.
        // origBlockSize = uncompressed block size, used for the threshold check.
        // Returns null if the result wouldn't improve on origBlockSize.
        private static byte[]? CompressHuffman(byte[] data, int origBlockSize)
        {
            if (data.Length == 0) return null;

            byte[] codeLens = BuildCodeLengths(data);
            int[] codes = AssignCanonicalCodes(codeLens);

            // Output layout: lz_size[4 LE]  code_lengths[256]  bitstream[...]
            const int HdrSize = 4 + HuffSyms;
            var outBuf = new byte[data.Length * 2 + HdrSize + 16];
            int opos = HdrSize;
            uint bitBuf = 0;
            int bitAvail = 0;

            void Flush()
            {
                while (bitAvail >= 8)
                {
                    if (opos >= outBuf.Length) Array.Resize(ref outBuf, outBuf.Length * 2);
                    outBuf[opos++] = (byte)(bitBuf & 0xFF);
                    bitBuf >>= 8; bitAvail -= 8;
                }
            }

            foreach (byte b in data)
            {
                int len = codeLens[b];
                if (len == 0) return null;   // symbol absent — shouldn't happen
                bitBuf |= (uint)codes[b] << bitAvail;
                bitAvail += len;
                Flush();
            }
            // Flush remaining bits
            if (bitAvail > 0)
            {
                if (opos >= outBuf.Length) Array.Resize(ref outBuf, outBuf.Length * 2);
                outBuf[opos++] = (byte)(bitBuf & 0xFF);
            }

            // Write header: lz_size[4] then code_lengths[256]
            int lzSize = data.Length;
            outBuf[0] = (byte)(lzSize);
            outBuf[1] = (byte)(lzSize >> 8);
            outBuf[2] = (byte)(lzSize >> 16);
            outBuf[3] = (byte)(lzSize >> 24);
            Array.Copy(codeLens, 0, outBuf, 4, HuffSyms);

            if (opos >= (int)(origBlockSize * CompLimit)) return null;

            var result = new byte[opos];
            Array.Copy(outBuf, result, opos);
            return result;
        }

        // Decompress Huffman + LZ pass.
        // Block layout: lz_size[4 LE]  code_lengths[256]  bitstream[...]
        private static byte[] DecompressHuffThenLz(byte[] data, int blockUncompSize, bool useLzss)
        {
            const int HdrSize = 4 + HuffSyms;
            if (data.Length <= HdrSize)
                throw new InvalidDataException("Huffman block too short for header.");

            // Read lz_size
            int lzSize = data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
            if (lzSize <= 0 || lzSize > data.Length * 8)
                throw new InvalidDataException("Huffman block lz_size out of range.");

            // Read code lengths
            var codeLens = new byte[HuffSyms];
            Array.Copy(data, 4, codeLens, 0, HuffSyms);

            // Build canonical decode table.
            // We use a flat table of size 2^HuffMaxBits.
            // Each entry: sym (byte) + len (byte).  len==0 means unused.
            int tableSize = 1 << HuffMaxBits;
            var decSym = new byte[tableSize];
            var decLen = new byte[tableSize];

            // Collect (len, sym) sorted (len ASC, sym ASC) — same order as encoder
            var pairs = new List<(int len, int sym)>(HuffSyms);
            for (int i = 0; i < HuffSyms; i++)
                if (codeLens[i] > 0) pairs.Add((codeLens[i], i));
            pairs.Sort((a, b) => a.len != b.len ? a.len.CompareTo(b.len) : a.sym.CompareTo(b.sym));

            int code = 0, prevLen = 0;
            foreach (var (len, sym) in pairs)
            {
                if (prevLen == 0)
                    code = 0;
                else
                    code = (code + 1) << (len - prevLen);

                // Fill all table entries that share this code prefix
                int step = 1 << (HuffMaxBits - len);
                int fill = code << (HuffMaxBits - len);
                int end = fill + step;
                for (int j = fill; j < end && j < tableSize; j++)
                {
                    decSym[j] = (byte)sym;
                    decLen[j] = (byte)len;
                }
                prevLen = len;
            }

            // Decode lzSize bytes from the bitstream
            var lzBuf = new byte[lzSize];
            int spos = HdrSize;
            int slen = data.Length;
            uint bitBuf = 0;
            int bitAvail = 0;
            int opos = 0;

            while (opos < lzSize)
            {
                // Refill
                while (bitAvail < HuffMaxBits && spos < slen)
                {
                    bitBuf |= (uint)data[spos++] << bitAvail;
                    bitAvail += 8;
                }
                int idx = (int)(bitBuf & (uint)(tableSize - 1));
                int clen = decLen[idx];
                if (clen == 0)
                    throw new InvalidDataException("Invalid Huffman code in block.");
                lzBuf[opos++] = decSym[idx];
                bitBuf >>= clen;
                bitAvail -= clen;
            }

            return useLzss
                ? DecompressLzss(lzBuf, blockUncompSize)
                : DecompressLz77(lzBuf, blockUncompSize);
        }

        // ── Unpack ───────────────────────────────────────────────────────────

        public static void Unpack(
            string xbaPath, string destDir,
            IProgress<ProgressReport>? progress = null,
            IProgress<LogEntry>? log = null,
            CancellationToken ct = default)
        {
            destDir = System.IO.Path.GetFullPath(destDir);
            Directory.CreateDirectory(destDir);

            using var fs = new FileStream(xbaPath, FileMode.Open, FileAccess.Read,
                                          FileShare.Read, 65536);
            using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: false);

            int ver = DetectVersionNum(br);

            if (ver == 3)
            {
                UnpackV3(br, destDir, progress, log, ct);
                return;
            }

            if (ver == 1)
            {
                UnpackV1(br, destDir, progress, log, ct);
                return;
            }

            // ver == 2
            UnpackV2(br, destDir, progress, log, ct);
        }

        // ── List ─────────────────────────────────────────────────────────────

        public static List<ArchiveEntry> List(string xbaPath)
        {
            var result = new List<ArchiveEntry>();
            using var fs = new FileStream(xbaPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: false);

            int ver = DetectVersionNum(br);
            if (ver == 3) return ListV3(br);
            if (ver == 1) { ListV1(br, result); return result; }

            // ver == 2
            ListV2(br, fs, result);
            return result;
        }

        // ── Test ─────────────────────────────────────────────────────────────

        public static TestResult Test(
            string xbaPath,
            IProgress<ProgressReport>? progress = null,
            CancellationToken ct = default)
        {
            int ok = 0, errors = 0;
            using var fs = new FileStream(xbaPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: false);

            int ver = DetectVersionNum(br);
            if (ver == 3) return TestV3(br, progress, ct);
            if (ver == 1)
            {
                TestV1(br, progress, ct, ref ok, ref errors);
                return new TestResult { Ok = ok, Errors = errors };
            }

            // ver == 2
            TestV2(br, progress, ct, ref ok, ref errors);
            return new TestResult { Ok = ok, Errors = errors };
        }

        // ── Public version probe ──────────────────────────────────────────
        // Returns 1, 2, or 3.  Throws InvalidDataException on bad/missing magic.
        public static int GetArchiveVersion(string xbaPath)
        {
            using var fs = new FileStream(xbaPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: false);
            return DetectVersionNum(br);
        }

        // Returns: 1=v1, 2=v2, 3=v3
        private static int DetectVersionNum(BinaryReader br)
        {
            var m = br.ReadBytes(4);
            if (m.Length < 4)
                throw new InvalidDataException("Not a valid XBA archive.");
            if (m[0] == MagicV3[0] && m[1] == MagicV3[1] &&
                m[2] == MagicV3[2] && m[3] == MagicV3[3])
                return 3;
            if (m[0] == MagicV2[0] && m[1] == MagicV2[1] &&
                m[2] == MagicV2[2] && m[3] == MagicV2[3])
                return 2;
            if (m[0] == MagicV1[0] && m[1] == MagicV1[1] &&
                m[2] == MagicV1[2] && m[3] == MagicV1[3])
                return 1;
            throw new InvalidDataException("Not a valid XBA archive.");
        }

        // Legacy bool helper for V1/V2 code paths
        private static bool DetectVersion(BinaryReader br)
        {
            int v = DetectVersionNum(br);
            if (v == 3) throw new InvalidDataException("Use Unpack/List/Test for V3 archives.");
            return v == 2;
        }

        private static List<(string rel, string abs, bool isDir)> CollectEntries(string srcDir)
        {
            var result = new List<(string, string, bool)>();
            Recurse(srcDir, srcDir, result);
            return result;
        }

        private static void Recurse(string root, string dir,
            List<(string, string, bool)> result)
        {
            var dirs = Directory.GetDirectories(dir);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs)
            {
                var rel = System.IO.Path.GetRelativePath(root, d).Replace('/', '\\');
                result.Add((rel, d, true));
                Recurse(root, d, result);
            }
            var files = Directory.GetFiles(dir);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                var rel = System.IO.Path.GetRelativePath(root, f).Replace('/', '\\');
                result.Add((rel, f, false));
            }
        }

        // ── RunParallel helper ────────────────────────────────────────────────
        // Runs work(i) for i in [0, count) on dedicated LongRunning threads,
        // NOT the shared ThreadPool. This avoids the starvation deadlock that
        // occurs when Parallel.For is called from inside Task.Run: both compete
        // for the same pool, the outer Task.Run thread blocks waiting for inner
        // work that can never start because the pool is full.
        //
        // Each thread is an OS thread created by TaskCreationOptions.LongRunning.
        // We create at most (ProcessorCount - 1) threads and partition the work
        // into that many chunks, each chunk processed sequentially on its thread.
        internal static void RunParallel(int count, CancellationToken ct, Action<int> work)
        {
            if (count <= 0) return;

            int threads = Math.Max(1, Math.Min(count, Environment.ProcessorCount - 1));
            int chunkSize = (count + threads - 1) / threads;

            var tasks = new System.Threading.Tasks.Task[threads];
            Exception? firstEx = null;
            object exLock = new object();

            for (int t = 0; t < threads; t++)
            {
                int start = t * chunkSize;
                int end = Math.Min(start + chunkSize, count);
                if (start >= count) { tasks[t] = Task.CompletedTask; continue; }

                tasks[t] = Task.Factory.StartNew(() =>
                {
                    for (int i = start; i < end; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (firstEx != null) break;
                        try { work(i); }
                        catch (Exception ex)
                        {
                            lock (exLock) { if (firstEx == null) firstEx = ex; }
                        }
                    }
                },
                ct,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            }

            Task.WaitAll(tasks);

            ct.ThrowIfCancellationRequested();
            if (firstEx != null) ExceptionDispatchInfo.Capture(firstEx).Throw();
        }
    }
}
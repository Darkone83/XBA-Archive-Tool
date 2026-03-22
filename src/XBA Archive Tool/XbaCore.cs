// XbaCore.cs — XBA archive format engine  v2
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

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
        public uint FilteredCrc { get; init; }  // CRC of assembled bytes BEFORE x86 unfilter
    }

    public sealed class TestResult
    {
        public int Ok { get; init; }
        public int Errors { get; init; }
    }

    public static class XbaCodec
    {
        // ── Format constants ─────────────────────────────────────────────────

        private static readonly byte[] MagicV1 = { (byte)'X', (byte)'B', (byte)'A', 0x01 };
        private static readonly byte[] MagicV2 = { (byte)'X', (byte)'B', (byte)'A', 0x02 };

        // v1 file flags
        private const byte V1FlagDir = 0x01;
        private const byte V1FlagLz77 = 0x00;
        private const byte V1FlagX86Lz = 0x02;
        private const byte V1FlagLzss = 0x03;
        private const byte V1FlagX86Ls = 0x04;

        // v2 file flags
        private const byte V2FlagDir = 0x01;
        private const byte V2FlagFile = 0x00;
        private const byte V2FlagX86 = 0x02;

        // v2 block flags
        private const byte BlkStored = 0x00;
        private const byte BlkLz77 = 0x01;
        private const byte BlkLzss = 0x02;
        private const byte BlkRle = 0x03;
        private const byte BlkLz77Huff = 0x04;
        private const byte BlkLzssHuff = 0x05;

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

        // ── v1 decompress dispatcher ─────────────────────────────────────────

        public static byte[] DecompressV1(byte flag, byte[] data, int uncompSize)
        {
            if (flag == V1FlagLzss || flag == V1FlagX86Ls)
                return DecompressLzss(data, uncompSize);
            return DecompressLz77(data, uncompSize);
        }

        // ── v2 block decompress dispatcher ───────────────────────────────────

        private static byte[] DecompressBlock(byte blockFlag, byte[] data, int blockUncompSize)
        {
            return blockFlag switch
            {
                BlkStored => data,
                BlkLz77 => DecompressLz77(data, blockUncompSize),
                BlkLzss => DecompressLzss(data, blockUncompSize),
                BlkRle => DecompressRle(data, blockUncompSize),
                BlkLz77Huff => DecompressHuffThenLz(data, blockUncompSize, false),
                BlkLzssHuff => DecompressHuffThenLz(data, blockUncompSize, true),
                _ => throw new InvalidDataException($"Unknown block flag 0x{blockFlag:X2}")
            };
        }

        // ── Best-of block compression ─────────────────────────────────────────
        //
        // Tries all six modes for a single block and returns the smallest
        // result along with its flag.

        private static (byte[] data, byte flag) BestBlock(byte[] block)
        {
            if (block.Length < StoreMin)
                return (block, BlkStored);

            byte[]? best = null;
            byte bestFlag = BlkStored;

            void Try(byte[] compressed, byte flag)
            {
                // CompressCore / CompressRle return the original array ref when
                // incompressible, so check for reference equality.
                if (ReferenceEquals(compressed, block)) return;
                if (compressed.Length >= block.Length * CompLimit) return;

                // Round-trip verify: if decompression doesn't reproduce the
                // source block exactly, this codec has a bug for this input.
                // Fall back to stored rather than bake corrupt data into the archive.
                try
                {
                    var verify = DecompressBlock(flag, compressed, block.Length);
                    if (verify.Length != block.Length) return;
                    for (int vi = 0; vi < block.Length; vi++)
                        if (verify[vi] != block[vi]) return;
                }
                catch { return; }

                if (best == null || compressed.Length < best.Length)
                { best = compressed; bestFlag = flag; }
            }

            byte[] lz77 = CompressLz77(block);
            byte[] lzss = CompressLzss(block);
            byte[] rle = CompressRle(block);

            Try(lz77, BlkLz77);
            Try(lzss, BlkLzss);
            Try(rle, BlkRle);

            // Huffman second-stage: only try on top of the best LZ result
            // to avoid redundant work.
            if (!ReferenceEquals(lz77, block))
            {
                byte[]? huff = CompressHuffman(lz77, block.Length);
                if (huff != null) Try(huff, BlkLz77Huff);
            }
            if (!ReferenceEquals(lzss, block))
            {
                byte[]? huff = CompressHuffman(lzss, block.Length);
                if (huff != null) Try(huff, BlkLzssHuff);
            }

            return best == null ? (block, BlkStored) : (best, bestFlag);
        }

        // ── Pack (v2) ────────────────────────────────────────────────────────

        public static void Pack(
            string srcDir, string outPath,
            IProgress<ProgressReport>? progress = null,
            IProgress<LogEntry>? log = null,
            CancellationToken ct = default)
        {
            var entries = CollectEntries(srcDir);
            int total = entries.Count;

            using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write,
                                          FileShare.None, 65536);
            using var bw = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: false);

            bw.Write(MagicV2);
            bw.Write(total);

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (rel, absPath, isDir) = entries[i];
                var pb = Encoding.ASCII.GetBytes(rel.Length > 255 ? rel[..255] : rel);

                if (isDir)
                {
                    bw.Write(V2FlagDir); bw.Write((byte)pb.Length); bw.Write(pb);
                    log?.Report(new LogEntry { Kind = "dir", FilePath = rel, IsV2 = true });
                }
                else
                {
                    var raw = File.ReadAllBytes(absPath);
                    uint crc = Crc32(raw);
                    // x86 filter disabled for V2: the per-block boundary problem means the
                    // extractor cannot perfectly reverse a whole-file filter pass. Since
                    // p.xbe compresses at ~100% ratio anyway the filter buys nothing.
                    bool applyX86 = false;
                    byte fileFlag = V2FlagFile;
                    var src_ = raw;

                    // Sanity check: verify x86 filter roundtrips correctly
                    if (applyX86)
                    {
                        var roundtrip = X86Unfilter(src_);
                        uint rtCrc = Crc32(roundtrip);
                        if (rtCrc != crc)
                            throw new InvalidDataException(
                                $"x86 filter roundtrip failed for {rel}: " +
                                $"expected {crc:X8} got {rtCrc:X8}");
                        // Log the filtered CRC so we can compare to assembled CRC on unpack
                        uint filteredCrc = Crc32(src_);
                        log?.Report(new LogEntry
                        {
                            Kind = "debug",
                            FilePath = rel,
                            ExpectedCrc = crc,
                            FilteredCrc = filteredCrc
                        });
                    }

                    // Split into blocks
                    int numBlocks = Math.Max(1, (src_.Length + BlockSize - 1) / BlockSize);
                    var blocks = new List<(byte[] data, byte flag)>(numBlocks);
                    long totalComp = 0;

                    for (int bi = 0; bi < numBlocks; bi++)
                    {
                        int off = bi * BlockSize;
                        int blen = Math.Min(BlockSize, src_.Length - off);
                        var blk = new byte[blen];
                        Array.Copy(src_, off, blk, 0, blen);
                        var (cdata, cflag) = BestBlock(blk);
                        blocks.Add((cdata, cflag));
                        totalComp += cdata.Length;

                        if (rel.EndsWith("p.xbe"))
                            System.Diagnostics.Debug.WriteLine(
                                $"PACK p.xbe blk{bi:D3} flag={cflag} csize={cdata.Length}" +
                                $" srcCrc={Crc32(blk, 0, blk.Length):X8}");
                    }

                    bw.Write(fileFlag);
                    bw.Write((byte)pb.Length);
                    bw.Write(pb);
                    bw.Write(raw.Length);   // uncomp_size (original, pre-filter)
                    bw.Write(crc);
                    bw.Write((short)numBlocks);

                    foreach (var (bdata, bflag) in blocks)
                    {
                        bw.Write(bflag);
                        bw.Write(bdata.Length);
                        bw.Write(bdata);
                    }

                    log?.Report(new LogEntry
                    {
                        Kind = "file",
                        FilePath = rel,
                        UncompSize = raw.Length,
                        CompSize = totalComp,
                        X86 = applyX86,
                        IsV2 = true
                    });
                }
                progress?.Report(new ProgressReport { Done = i + 1, Total = total, CurrentFile = rel });
            }
        }


        // ── Pack (v1) ────────────────────────────────────────────────────────
        //
        // V1 format: one entry per file, LZ77 or LZSS or stored, no blocks.
        // Files larger than the LZ window compress with LZSS (32KB window).
        // x86 filter applied before compression for .xbe/.exe/.dll/.sys/.ocx.
        // CRC32 is of the original pre-filter data.
        // Compatible with the simple Xbox-side Extract_V1 path.

        public static void PackV1(
            string srcDir, string outPath,
            IProgress<ProgressReport>? progress = null,
            IProgress<LogEntry>? log = null,
            CancellationToken ct = default)
        {
            var entries = CollectEntries(srcDir);
            int total = entries.Count;

            using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write,
                                          FileShare.None, 65536);
            using var bw = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: false);

            bw.Write(MagicV1);
            bw.Write(total);

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (rel, absPath, isDir) = entries[i];
                var pb = Encoding.ASCII.GetBytes(rel.Length > 255 ? rel[..255] : rel);

                if (isDir)
                {
                    bw.Write(V1FlagDir);
                    bw.Write((byte)pb.Length);
                    bw.Write(pb);
                    log?.Report(new LogEntry { Kind = "dir", FilePath = rel, IsV2 = false });
                }
                else
                {
                    var raw = File.ReadAllBytes(absPath);
                    uint crc = Crc32(raw);
                    bool applyX86 = X86Exts.Contains(System.IO.Path.GetExtension(rel));
                    bool useLzss = raw.Length > LzWin;

                    // V1 Xbox extractor buffers are capped at BlockSize (65536).
                    // Files larger than BlockSize must be stored raw -- no filter, no compression.
                    // x86 filter is only applied when we will actually compress the result.
                    byte fileFlag;
                    byte[] comp;
                    bool stored;

                    if (raw.Length > BlockSize)
                    {
                        // Too large for compressed path -- store raw, no filter
                        stored = true;
                        fileFlag = V1FlagLz77;   // stored flag (compSize==uncompSize signals stored)
                        comp = raw;
                    }
                    else
                    {
                        // Try compression with optional x86 pre-filter
                        var src_ = applyX86 ? X86Filter(raw) : raw;

                        if (applyX86)
                            fileFlag = useLzss ? V1FlagX86Ls : V1FlagX86Lz;
                        else
                            fileFlag = useLzss ? V1FlagLzss : V1FlagLz77;

                        comp = useLzss ? CompressLzss(src_) : CompressLz77(src_);
                        stored = false;

                        if (ReferenceEquals(comp, src_) || comp.Length >= src_.Length * CompLimit
                            || comp.Length > BlockSize)
                        {
                            stored = true;
                        }
                        else
                        {
                            try
                            {
                                var verify = DecompressV1(fileFlag, comp, src_.Length);
                                for (int vi = 0; vi < src_.Length; vi++)
                                    if (verify[vi] != src_[vi]) { stored = true; break; }
                            }
                            catch { stored = true; }
                        }

                        if (stored)
                        {
                            // Compression failed or not beneficial -- store raw (no filter)
                            comp = raw;
                            fileFlag = V1FlagLz77;
                        }
                    }

                    bw.Write(fileFlag);
                    bw.Write((byte)pb.Length);
                    bw.Write(pb);
                    bw.Write(raw.Length);   // uncomp_size always original raw length
                    bw.Write(comp.Length);  // comp_size == raw.Length when stored
                    bw.Write(crc);          // CRC always of raw (pre-filter) data
                    bw.Write(comp);

                    log?.Report(new LogEntry
                    {
                        Kind = "file",
                        FilePath = rel,
                        UncompSize = raw.Length,
                        CompSize = comp.Length,
                        X86 = applyX86,
                        Lzss = useLzss && !stored,
                        IsV2 = false
                    });
                }
                progress?.Report(new ProgressReport { Done = i + 1, Total = total, CurrentFile = rel });
            }
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

            bool isV2 = DetectVersion(br);
            int ec = br.ReadInt32();

            for (int i = 0; i < ec; i++)
            {
                ct.ThrowIfCancellationRequested();
                byte flag = br.ReadByte();
                int pl = br.ReadByte();
                var rel = Encoding.ASCII.GetString(br.ReadBytes(pl));
                var ap = System.IO.Path.Combine(destDir,
                                rel.Replace('\\', System.IO.Path.DirectorySeparatorChar));

                bool isDir = isV2 ? flag == V2FlagDir : flag == V1FlagDir;

                if (isDir)
                {
                    Directory.CreateDirectory(ap);
                    log?.Report(new LogEntry { Kind = "dir", FilePath = rel, IsV2 = isV2 });
                }
                else if (isV2)
                {
                    int usize = br.ReadInt32();
                    uint crc = br.ReadUInt32();
                    int blockCount = br.ReadInt16();
                    var assembled = new byte[usize];
                    int outOff = 0;

                    var blockCrcs = new uint[blockCount];
                    for (int bi = 0; bi < blockCount; bi++)
                    {
                        byte bflag = br.ReadByte();
                        int csize = br.ReadInt32();
                        var bdata = br.ReadBytes(csize);
                        int blockUncomp = bi < blockCount - 1
                            ? BlockSize
                            : usize - (blockCount - 1) * BlockSize;

                        var decoded = DecompressBlock(bflag, bdata, blockUncomp);
                        Array.Copy(decoded, 0, assembled, outOff, decoded.Length);
                        blockCrcs[bi] = Crc32(decoded, 0, decoded.Length);

                        if (rel == "p.xbe")
                            System.Diagnostics.Debug.WriteLine(
                                $"UNPACK p.xbe blk{bi:D3} flag={bflag} csize={csize}" +
                                $" decoded={decoded.Length} crc={blockCrcs[bi]:X8}");

                        outOff += decoded.Length;
                    }

                    // Verify assembled matches expected filtered source
                    uint preUnfilterCrc = Crc32(assembled);

                    if (flag == V2FlagX86)
                    {
                        uint assembledCrc = Crc32(assembled);
                        assembled = X86Unfilter(assembled);
                        uint unfilterCrc = Crc32(assembled);
                        if (unfilterCrc != crc)
                        {
                            log?.Report(new LogEntry
                            {
                                Kind = "error",
                                FilePath = rel,
                                UncompSize = usize,
                                IsV2 = true,
                                ActualCrc = unfilterCrc,
                                ExpectedCrc = crc,
                                FilteredCrc = assembledCrc
                            });
                            continue;
                        }
                    }
                    else
                    {
                        uint gotCrc = Crc32(assembled);
                        if (gotCrc != crc)
                        {
                            log?.Report(new LogEntry
                            {
                                Kind = "error",
                                FilePath = rel,
                                UncompSize = usize,
                                IsV2 = true,
                                ActualCrc = gotCrc,
                                ExpectedCrc = crc
                            });
                            continue;
                        }
                    }

                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ap)!);
                    File.WriteAllBytes(ap, assembled);
                    log?.Report(new LogEntry
                    {
                        Kind = "file",
                        FilePath = rel,
                        UncompSize = usize,
                        X86 = flag == V2FlagX86,
                        IsV2 = true
                    });
                }
                else
                {
                    // v1 path
                    int usize = br.ReadInt32();
                    int csize = br.ReadInt32();
                    uint crc = br.ReadUInt32();
                    var raw = br.ReadBytes(csize);

                    byte[] decoded = csize == usize ? raw : DecompressV1(flag, raw, usize);
                    if (flag == V1FlagX86Lz || flag == V1FlagX86Ls)
                        decoded = X86Unfilter(decoded);

                    if (Crc32(decoded) != crc)
                        log?.Report(new LogEntry
                        {
                            Kind = "error",
                            FilePath = rel,
                            UncompSize = usize,
                            CompSize = csize
                        });
                    else
                    {
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ap)!);
                        File.WriteAllBytes(ap, decoded);
                        log?.Report(new LogEntry
                        {
                            Kind = "file",
                            FilePath = rel,
                            UncompSize = usize,
                            CompSize = csize,
                            X86 = flag == V1FlagX86Lz || flag == V1FlagX86Ls,
                            Lzss = flag == V1FlagLzss || flag == V1FlagX86Ls
                        });
                    }
                }
                progress?.Report(new ProgressReport { Done = i + 1, Total = ec, CurrentFile = rel });
            }
        }

        // ── List ─────────────────────────────────────────────────────────────

        public static List<ArchiveEntry> List(string xbaPath)
        {
            var result = new List<ArchiveEntry>();
            using var fs = new FileStream(xbaPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: false);

            bool isV2 = DetectVersion(br);
            int ec = br.ReadInt32();

            for (int i = 0; i < ec; i++)
            {
                byte flag = br.ReadByte();
                int pl = br.ReadByte();
                var rel = Encoding.ASCII.GetString(br.ReadBytes(pl));

                bool isDir = isV2 ? flag == V2FlagDir : flag == V1FlagDir;

                if (isDir)
                {
                    result.Add(new ArchiveEntry
                    {
                        Type = EntryType.Directory,
                        Path = rel,
                        IsV2 = isV2
                    });
                }
                else if (isV2)
                {
                    int usize = br.ReadInt32();
                    uint crc = br.ReadUInt32();
                    int blockCount = br.ReadInt16();
                    long totalComp = 0;

                    for (int bi = 0; bi < blockCount; bi++)
                    {
                        br.ReadByte();              // block_flag
                        int csize = br.ReadInt32();
                        totalComp += csize;
                        fs.Seek(csize, SeekOrigin.Current);
                    }

                    result.Add(new ArchiveEntry
                    {
                        Type = EntryType.File,
                        Path = rel,
                        UncompSize = usize,
                        CompSize = totalComp,
                        Crc32 = crc,
                        X86Filter = flag == V2FlagX86,
                        BlockCount = blockCount,
                        IsV2 = true
                    });
                }
                else
                {
                    int usize = br.ReadInt32();
                    int csize = br.ReadInt32();
                    uint crc = br.ReadUInt32();
                    fs.Seek(csize, SeekOrigin.Current);
                    result.Add(new ArchiveEntry
                    {
                        Type = EntryType.File,
                        Path = rel,
                        UncompSize = usize,
                        CompSize = csize,
                        Crc32 = crc,
                        X86Filter = flag == V1FlagX86Lz || flag == V1FlagX86Ls,
                        UsesLzss = flag == V1FlagLzss || flag == V1FlagX86Ls,
                        IsV2 = false
                    });
                }
            }
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

            bool isV2 = DetectVersion(br);
            int ec = br.ReadInt32();

            for (int i = 0; i < ec; i++)
            {
                ct.ThrowIfCancellationRequested();
                byte flag = br.ReadByte();
                int pl = br.ReadByte();
                var rel = Encoding.ASCII.GetString(br.ReadBytes(pl));

                bool isDir = isV2 ? flag == V2FlagDir : flag == V1FlagDir;

                if (!isDir)
                {
                    if (isV2)
                    {
                        int usize = br.ReadInt32();
                        uint crc = br.ReadUInt32();
                        int blockCount = br.ReadInt16();
                        var assembled = new byte[usize];
                        int outOff = 0;
                        bool blockErr = false;

                        for (int bi = 0; bi < blockCount; bi++)
                        {
                            byte bflag = br.ReadByte();
                            int csize = br.ReadInt32();
                            var bdata = br.ReadBytes(csize);
                            int blockUncomp = bi < blockCount - 1
                                ? BlockSize
                                : usize - (blockCount - 1) * BlockSize;
                            try
                            {
                                var dec = DecompressBlock(bflag, bdata, blockUncomp);
                                Array.Copy(dec, 0, assembled, outOff, dec.Length);
                                outOff += dec.Length;
                            }
                            catch { blockErr = true; break; }
                        }

                        if (!blockErr && flag == V2FlagX86)
                            assembled = X86Unfilter(assembled);

                        if (!blockErr && Crc32(assembled) == crc) ok++; else errors++;
                    }
                    else
                    {
                        int usize = br.ReadInt32();
                        int csize = br.ReadInt32();
                        uint crc = br.ReadUInt32();
                        var raw = br.ReadBytes(csize);
                        byte[] dec = csize == usize ? raw : DecompressV1(flag, raw, usize);
                        if (flag == V1FlagX86Lz || flag == V1FlagX86Ls)
                            dec = X86Unfilter(dec);
                        if (Crc32(dec) == crc) ok++; else errors++;
                    }
                }
                progress?.Report(new ProgressReport { Done = i + 1, Total = ec, CurrentFile = rel });
            }
            return new TestResult { Ok = ok, Errors = errors };
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        // Reads the 4-byte magic and returns true for v2, false for v1.
        // Throws InvalidDataException on unknown magic.
        private static bool DetectVersion(BinaryReader br)
        {
            var m = br.ReadBytes(4);
            if (m.Length < 4)
                throw new InvalidDataException("Not a valid XBA archive.");
            if (m[0] == MagicV2[0] && m[1] == MagicV2[1] &&
                m[2] == MagicV2[2] && m[3] == MagicV2[3])
                return true;
            if (m[0] == MagicV1[0] && m[1] == MagicV1[1] &&
                m[2] == MagicV1[2] && m[3] == MagicV1[3])
                return false;
            throw new InvalidDataException("Not a valid XBA archive.");
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
    }
}
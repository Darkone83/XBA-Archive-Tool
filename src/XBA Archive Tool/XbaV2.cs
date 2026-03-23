// XbaV2.cs — XBA v2 format pack/unpack/list/test
//
// V2 format: blocked compression, multiple codecs per block.
// Each file is split into 64 KB blocks; each block is independently
// compressed with the best of: stored, LZ77, LZSS, RLE, LZ77+Huffman,
// LZSS+Huffman. x86 filter is disabled (whole-file filter cannot be
// reversed cleanly at per-block boundaries).
// CRC32 is of the original pre-filter uncompressed data.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XbaTool
{
    public static partial class XbaCodec
    {
        // ── V2 format constants ───────────────────────────────────────────────

        private static readonly byte[] MagicV2 = { (byte)'X', (byte)'B', (byte)'A', 0x02 };

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

        // ── V2 block decompress dispatcher ────────────────────────────────────

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

        // ── Pack (v2) ─────────────────────────────────────────────────────────

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
                if (rel.Length > 255)
                    throw new InvalidOperationException($"Path exceeds 255-character limit: {rel}");
                var pb = Encoding.ASCII.GetBytes(rel);

                if (isDir)
                {
                    bw.Write(V2FlagDir); bw.Write((byte)pb.Length); bw.Write(pb);
                    log?.Report(new LogEntry { Kind = "dir", FilePath = rel, IsV2 = true });
                }
                else
                {
                    var raw = File.ReadAllBytes(absPath);
                    uint crc = Crc32(raw);
                    byte fileFlag = V2FlagFile;

                    int numBlocks = Math.Max(1, (raw.Length + BlockSize - 1) / BlockSize);
                    var blocks = new (byte[] data, byte flag)[numBlocks];

                    // Compress blocks in parallel — each block is independent.
                    RunParallel(numBlocks, ct, bi =>
                    {
                        int off = bi * BlockSize;
                        int blen = Math.Min(BlockSize, raw.Length - off);
                        var blk = new byte[blen];
                        Array.Copy(raw, off, blk, 0, blen);
                        blocks[bi] = BestBlock(blk);
                    });

                    long totalComp = 0;
                    foreach (var (bdata, _) in blocks) totalComp += bdata.Length;

                    bw.Write(fileFlag);
                    bw.Write((byte)pb.Length);
                    bw.Write(pb);
                    bw.Write(raw.Length);
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
                        IsV2 = true
                    });
                }
                progress?.Report(new ProgressReport { Done = i + 1, Total = total, CurrentFile = rel });
            }
        }

        // ── Unpack (v2) ───────────────────────────────────────────────────────

        internal static void UnpackV2(
            BinaryReader br, string destDir,
            IProgress<ProgressReport>? progress,
            IProgress<LogEntry>? log,
            CancellationToken ct)
        {
            int ec = br.ReadInt32();

            for (int i = 0; i < ec; i++)
            {
                ct.ThrowIfCancellationRequested();
                byte flag = br.ReadByte();
                int pl = br.ReadByte();
                var rel = Encoding.ASCII.GetString(br.ReadBytes(pl));
                var ap = System.IO.Path.Combine(destDir,
                                rel.Replace('\\', System.IO.Path.DirectorySeparatorChar));

                if (flag == V2FlagDir)
                {
                    Directory.CreateDirectory(ap);
                    log?.Report(new LogEntry { Kind = "dir", FilePath = rel, IsV2 = true });
                }
                else
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
                progress?.Report(new ProgressReport { Done = i + 1, Total = ec, CurrentFile = rel });
            }
        }

        // ── List (v2) ─────────────────────────────────────────────────────────

        internal static void ListV2(BinaryReader br, FileStream fs, List<ArchiveEntry> result)
        {
            int ec = br.ReadInt32();

            for (int i = 0; i < ec; i++)
            {
                byte flag = br.ReadByte();
                int pl = br.ReadByte();
                var rel = Encoding.ASCII.GetString(br.ReadBytes(pl));

                if (flag == V2FlagDir)
                {
                    result.Add(new ArchiveEntry
                    {
                        Type = EntryType.Directory,
                        Path = rel,
                        IsV2 = true
                    });
                }
                else
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
            }
        }

        // ── Test (v2) ─────────────────────────────────────────────────────────

        internal static void TestV2(
            BinaryReader br,
            IProgress<ProgressReport>? progress,
            CancellationToken ct,
            ref int ok, ref int errors)
        {
            int ec = br.ReadInt32();

            for (int i = 0; i < ec; i++)
            {
                ct.ThrowIfCancellationRequested();
                byte flag = br.ReadByte();
                int pl = br.ReadByte();
                var rel = Encoding.ASCII.GetString(br.ReadBytes(pl));

                if (flag != V2FlagDir)
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
                progress?.Report(new ProgressReport { Done = i + 1, Total = ec, CurrentFile = rel });
            }
        }
    }
}
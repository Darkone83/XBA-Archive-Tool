// XbaV1.cs — XBA v1 format pack/unpack/list/test
//
// V1 format: one entry per file, LZ77 or LZSS or stored, no blocks.
// Files larger than the LZ window compress with LZSS (32 KB window).
// x86 filter applied before compression for .xbe/.exe/.dll/.sys/.ocx.
// CRC32 is of the original pre-filter data.
// Compatible with the simple Xbox-side Extract_V1 path.

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
        // ── V1 format constants ───────────────────────────────────────────────

        private static readonly byte[] MagicV1 = { (byte)'X', (byte)'B', (byte)'A', 0x01 };

        // v1 file flags
        private const byte V1FlagDir = 0x01;
        private const byte V1FlagLz77 = 0x00;
        private const byte V1FlagX86Lz = 0x02;
        private const byte V1FlagLzss = 0x03;
        private const byte V1FlagX86Ls = 0x04;

        // ── V1 decompress dispatcher ──────────────────────────────────────────

        public static byte[] DecompressV1(byte flag, byte[] data, int uncompSize)
        {
            if (flag == V1FlagLzss || flag == V1FlagX86Ls)
                return DecompressLzss(data, uncompSize);
            return DecompressLz77(data, uncompSize);
        }

        // ── Pack (v1) ─────────────────────────────────────────────────────────

        // Compressed result for one V1 entry (file or directory).
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
                if (rel.Length > 255)
                    throw new InvalidOperationException($"Path exceeds 255-character limit: {rel}");
                var pb = Encoding.ASCII.GetBytes(rel);

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

                    byte fileFlag;
                    byte[] comp;
                    bool stored;

                    if (raw.Length > BlockSize)
                    {
                        stored = true;
                        fileFlag = V1FlagLz77;
                        comp = raw;
                    }
                    else
                    {
                        var src_ = applyX86 ? X86Filter(raw) : raw;

                        fileFlag = applyX86
                            ? (useLzss ? V1FlagX86Ls : V1FlagX86Lz)
                            : (useLzss ? V1FlagLzss : V1FlagLz77);

                        comp = useLzss ? CompressLzss(src_) : CompressLz77(src_);
                        stored = ReferenceEquals(comp, src_)
                              || comp.Length >= src_.Length * CompLimit
                              || comp.Length > BlockSize;

                        if (!stored)
                        {
                            try
                            {
                                var verify = DecompressV1(fileFlag, comp, src_.Length);
                                for (int vi = 0; vi < src_.Length; vi++)
                                    if (verify[vi] != src_[vi]) { stored = true; break; }
                            }
                            catch { stored = true; }
                        }

                        if (stored) { comp = raw; fileFlag = V1FlagLz77; }
                    }

                    bw.Write(fileFlag);
                    bw.Write((byte)pb.Length);
                    bw.Write(pb);
                    bw.Write(raw.Length);
                    bw.Write(comp.Length);
                    bw.Write(crc);
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

        // ── Unpack (v1) ───────────────────────────────────────────────────────

        internal static void UnpackV1(
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

                if (flag == V1FlagDir)
                {
                    Directory.CreateDirectory(ap);
                    log?.Report(new LogEntry { Kind = "dir", FilePath = rel, IsV2 = false });
                }
                else
                {
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

        // ── List (v1) ─────────────────────────────────────────────────────────

        internal static void ListV1(BinaryReader br, List<ArchiveEntry> result)
        {
            int ec = br.ReadInt32();

            for (int i = 0; i < ec; i++)
            {
                byte flag = br.ReadByte();
                int pl = br.ReadByte();
                var rel = Encoding.ASCII.GetString(br.ReadBytes(pl));

                if (flag == V1FlagDir)
                {
                    result.Add(new ArchiveEntry
                    {
                        Type = EntryType.Directory,
                        Path = rel,
                        IsV2 = false
                    });
                }
                else
                {
                    int usize = br.ReadInt32();
                    int csize = br.ReadInt32();
                    uint crc = br.ReadUInt32();
                    br.BaseStream.Seek(csize, SeekOrigin.Current);
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
        }

        // ── Test (v1) ─────────────────────────────────────────────────────────

        internal static void TestV1(
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

                if (flag != V1FlagDir)
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
                progress?.Report(new ProgressReport { Done = i + 1, Total = ec, CurrentFile = rel });
            }
        }
    }
}
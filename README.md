<div align-center>
  <img src="https://github.com/Darkone83/XBA-Archive-Tool/blob/main/img/xba.png" width=275> <img src="https://github.com/Darkone83/XBA-Archive-Tool/blob/main/img/Darkone83.png" width=350>

  <img src="https://github.com/Darkone83/XBA-Archive-Tool/blob/main/img/Main.png">
</div>

# XBA Archive Tool

A Windows WPF tool for creating, inspecting, and extracting `.xba` archives — a custom compressed archive format designed for deployment on original Xbox hardware.

Developed by **Darkone83**.

---

## Features

- Pack a directory tree into a V1, V2, or V3 `.xba` archive
- Unpack an archive to a destination directory
- Inspect archive contents with per-entry type, codec, block count, size, ratio, and CRC32
- Integrity-test an archive (full CRC verification without extracting to disk)
- Drag-and-drop support for opening archives and packing directories
- Automatic post-pack integrity check

---

## Usage

### Pack

1. Click **Pack** (or drag a folder onto the window)
2. Select the **source directory** to pack
3. Choose the **output `.xba` filename**
4. Select the archive version using the **V1 / V2 / V3 toggle** in the toolbar
5. The tool packs, then immediately runs a full integrity test on the result

### Unpack

1. Open an archive via **Open** or drag-and-drop
2. Click **Unpack**
3. Select a destination directory
4. CRC errors (if any) are reported per file after extraction

### Test

Verifies every file entry's CRC32 without writing to disk. Useful for checking archive integrity before deploying to Xbox.

### Inspect

Opening an archive populates the file list with:

| Column     | Description                                              |
|------------|----------------------------------------------------------|
| Name       | Relative path within the archive (backslash-separated)  |
| Type       | `DIR v1`, `FILE v1`, `DIR v2`, `FILE v2`, or `FILE v3`  |
| Filter     | Pre-compression filter applied to the file (V3)         |
| Blocks     | Number of compressed blocks (V2 and V3 files)           |
| Size       | Uncompressed file size                                   |
| Compressed | Compressed size stored in the archive                    |
| Ratio      | Compressed / uncompressed, or `stored` if no compression |
| CRC32      | CRC32 of the original uncompressed data                  |

---

## Archive Format

XBA uses a magic-versioned sequential container. All multi-byte integers are **little-endian**. Paths use **backslash separators**, are **ASCII-encoded**, and are **relative with no leading backslash**.

### Magic bytes

| Version | Magic (4 bytes hex)       |
|---------|---------------------------|
| V1      | `58 42 41 01` (`XBA\x01`) |
| V2      | `58 42 41 02` (`XBA\x02`) |
| V3      | `58 42 41 03` (`XBA\x03`) |

### Common header (V1 / V2)

```
[4]  magic         "XBA\x01" or "XBA\x02"
[4]  entry_count   uint32 LE  — total entries (files + directories)
```

---

### V1 Format (`XBA\x01`)

Designed for simplicity. Each file is stored as a single compressed or stored blob. Suitable for small payloads where all files fit within the 65,536-byte decompressor buffer.

#### Per-entry layout

```
[1]  flags      file type and codec (see below)
[1]  path_len   uint8, length of the path string
[N]  path       ASCII path, path_len bytes

If flags != 0x01 (not a directory):
  [4]  uncomp_size   uint32 LE  — original uncompressed size
  [4]  comp_size     uint32 LE  — compressed size; equals uncomp_size if stored
  [4]  crc32         uint32 LE  — CRC32 of the original pre-filter data
  [N]  data          comp_size bytes of compressed or raw data
```

#### V1 file flags

| Value  | Meaning                        |
|--------|--------------------------------|
| `0x00` | Plain file, LZ77 or stored     |
| `0x01` | Directory entry                |
| `0x02` | x86-filtered file, LZ77        |
| `0x03` | Plain file, LZSS               |
| `0x04` | x86-filtered file, LZSS        |

`comp_size == uncomp_size` signals a stored (uncompressed) entry regardless of the flags field.

#### V1 compression algorithms

**LZ77** — used for files ≤ 16,384 bytes  
- 16 KB ring buffer (window), zero-initialised  
- Control byte per 8 symbols, LSB-first  
- `0` bit → literal byte  
- `1` bit → 2-byte match token: `bits[0:13]` = ring-buffer offset, `bits[14:17]` = length−3 (lengths 3–18)

**LZSS** — used for files > 16,384 bytes  
- 32 KB ring buffer, zero-initialised  
- Control byte per 8 symbols, LSB-first  
- `0` bit → literal byte  
- `1` bit → 3-byte match token: `bits[0:14]` = offset, `bits[15:20]` = length−2 (lengths 2–65)

#### V1 x86 branch filter

Applied to `.xbe`, `.exe`, `.dll`, `.sys`, `.ocx` files **before** compression (only when the compressed result is smaller than the original and fits within 65,536 bytes; otherwise the file is stored raw without filtering).

For each byte at position `i` where `data[i]` is `0xE8` (CALL) or `0xE9` (JMP) and `i + 4 < file_length`:
```
stored_operand = original_relative_operand + (i + 5)
```
The extractor reverses this:
```
restored_operand = stored_operand - (i + 5)
```

CRC32 is always computed on the **original pre-filter data**.

#### V1 Xbox extractor constraint

The Xbox-side extractor (`xba.cpp`) uses static 65,536-byte buffers for decompressed output. **Compressed files must therefore decompress to ≤ 65,536 bytes.** Files larger than this limit are automatically stored uncompressed in the archive (no filter, no compression). The packer enforces this: if the compressed result would exceed the buffer, the file is stored raw.

---

### V2 Format (`XBA\x02`)

Extends V1 with a block-based multi-codec design. Each file is split into 65,536-byte blocks, each compressed independently with the best available codec. This removes the file-size limit for extraction and enables per-block codec selection.

#### Per-entry layout

```
[1]  file_flag     file type and filter (see below)
[1]  path_len      uint8
[N]  path          ASCII path

If file_flag != 0x01 (not a directory):
  [4]  uncomp_size   uint32 LE  — total uncompressed file size
  [4]  crc32         uint32 LE  — CRC32 of the final decoded data
  [2]  block_count   uint16 LE  — number of compressed blocks

  Per block (block_count entries):
    [1]  block_flag   codec used for this block (see below)
    [4]  comp_size    uint32 LE  — compressed byte count for this block
    [N]  data         comp_size bytes
```

#### V2 file flags

| Value  | Meaning                                         |
|--------|-------------------------------------------------|
| `0x00` | Plain file, no filter                           |
| `0x01` | Directory entry                                 |
| `0x02` | x86-filtered file *(reserved; see note below)* |

> **Note:** The x86 filter (`0x02`) is defined in the format but is **not emitted by the current packer**. Because the filter operates on the full file before blocking, reversing it correctly on a block-by-block basis at the extractor requires cross-block boundary handling that is not yet implemented. Packing with `file_flag = 0x02` will produce incorrect extraction results. The flag is reserved for a future version of the format.

#### V2 block flags

| Value  | Codec                    |
|--------|--------------------------|
| `0x00` | Stored (no compression)  |
| `0x01` | LZ77                     |
| `0x02` | LZSS                     |
| `0x03` | RLE                      |
| `0x04` | LZ77 + canonical Huffman |
| `0x05` | LZSS + canonical Huffman |

Each block decompresses to exactly **65,536 bytes**, except the last block which decompresses to `uncomp_size − (block_count − 1) × 65536` bytes.

#### V2 block codec details

**Stored (`0x00`):** Raw bytes, `comp_size == block_uncomp_size`.

**LZ77 (`0x01`):** Same algorithm as V1 LZ77 above, applied per block.

**LZSS (`0x02`):** Same algorithm as V1 LZSS above, applied per block.

**RLE (`0x03`):**  
Control byte per run, LSB interpretation:  
- `ctrl & 0x80` set → literal run: next `(ctrl & 0x7F) + 1` bytes are literals  
- `ctrl & 0x80` clear → repeat run: next byte repeated `(ctrl & 0x7F) + 1` times

**LZ77 + Huffman (`0x04`) and LZSS + Huffman (`0x05`):**

The block payload is structured as:
```
[4]   lz_size       uint32 LE — byte count of the LZ-compressed intermediate
[256] code_lengths  one byte per symbol 0–255; 0 = symbol absent from tree
[...] bitstream     remainder of comp_size bytes, packed LSB-first
```

1. The bitstream is Huffman-decoded using canonical codes rebuilt from `code_lengths`, producing `lz_size` bytes of LZ data.
2. That LZ data is then decompressed with LZ77 (flag `0x04`) or LZSS (flag `0x05`) to produce the final block output.

**Canonical Huffman code assignment:**  
Symbols sorted by (code_length ascending, symbol value ascending). Codes assigned sequentially per length level, left-shifted when length increases. Lookup table is a flat 32,768-entry (2^15) direct-index table keyed on the next 15 bits of the bitstream LSB-first.

#### V2 CRC32

Computed on the **final decoded data** (all blocks reassembled in order, after any filter reversal). Uses the standard IEEE 802.3 polynomial (`0xEDB88320` reflected).

---

### V3 Format (`XBA\x03`)

V3 is the archival-grade format. It introduces a separated TOC, whole-file pre-filters, variable block sizes, and a full modern codec contest per block (Deflate, strided Deflate, Brotli, Zstd). Files-only — no directory entries in the data stream; directory hierarchy is encoded entirely in the path strings.

#### Header

```
[4]  magic          "XBA\x03"
[4]  entry_count    uint32 LE  — number of file entries (no directory entries)
[4]  toc_offset     uint32 LE  — byte offset of the TOC from start of file
[4]  flags          uint32 LE  — archive-level flags (see below)
```

#### Archive-level flags

| Bit | Meaning                   |
|-----|---------------------------|
| `0` | Solid blocks (reserved)   |

#### Per-entry layout

Entries are written sequentially after the header, each at the offset recorded in the TOC.

```
[1]  filter_flag    pre-compression filter applied to the whole file (see below)
[1]  path_len       uint8
[N]  path           ASCII path, path_len bytes
[4]  uncomp_size    uint32 LE  — total uncompressed file size
[4]  crc32          uint32 LE  — CRC32 of the final decoded + unfiltered data
[2]  block_count    uint16 LE  — number of compressed blocks
[4]  block_size     uint32 LE  — decompressed size of each non-last block

Per block (block_count entries):
  [1]  block_flag   codec used for this block (see below)
  [4]  comp_size    uint32 LE  — compressed byte count for this block
  [N]  data         comp_size bytes
```

The last block decompresses to `uncomp_size − (block_count − 1) × block_size` bytes. All other blocks decompress to exactly `block_size` bytes.

#### V3 filter flags

Applied to the **entire file** before blocking and compression. The reverse filter is applied after all blocks are decompressed and reassembled.

| Value  | Filter                                          |
|--------|-------------------------------------------------|
| `0x00` | None                                            |
| `0x01` | x86 branch filter (same algorithm as V1/V2)     |
| `0x02` | Delta-8 (byte-level delta encoding)             |
| `0x03` | Delta-16 (16-bit little-endian delta encoding)  |
| `0x04` | Delta-32 (32-bit little-endian delta encoding)  |
| `0x05` | Stereo delta (interleaved L/R channel delta)    |
| `0xFF` | Solid block marker (reserved)                   |

CRC32 is always computed on the **final decoded + unfiltered data**.

#### V3 block flags

| Value  | Codec                                      |
|--------|--------------------------------------------|
| `0x00` | Stored (no compression)                    |
| `0x01` | LZ77                                       |
| `0x02` | LZSS                                       |
| `0x03` | Deflate (raw, no zlib/gzip framing)        |
| `0x04` | Strided Deflate (stride-split + Deflate)   |
| `0x05` | Brotli (quality 7, window 22)              |
| `0x06` | Zstd (level 15, standard zstd frame)       |

#### V3 block codec details

**Stored (`0x00`):** Raw bytes, `comp_size == block_uncomp_size`.

**LZ77 (`0x01`) / LZSS (`0x02`):** Same algorithms as V1/V2 above, applied per block.

**Deflate (`0x03`):** Raw DEFLATE bitstream (RFC 1951), no zlib or gzip framing. Compressed with .NET `DeflateStream` at `SmallestSize`.

**Strided Deflate (`0x04`):** Block payload is:
```
[1]  stride    uint8 — stride width in bytes (2, 4, or 8)
[N]  data      raw DEFLATE of the stride-split block
```
The block bytes are de-interleaved by `stride` before compression (all bytes at offset `%2`, then `%4`, etc.), then Deflate-compressed. The decompressor inflates the data then re-interleaves to restore original byte order. Effective for structured numeric data (floats, vectors, PCM audio).

**Brotli (`0x05`):** Raw Brotli-compressed bytes (RFC 7932). No additional framing — V3 already stores `comp_size` and `uncomp_size` per block. Compressed at quality 7, window size 22 (4 MB). Decompressed with `BrotliDecoder.TryDecompress`.

**Zstd (`0x06`):** Standard zstd frame (RFC 8878), including magic number and frame header. Compressed at level 15. Decompressed with `ZstdSharp.Port` `Decompressor.Unwrap`.

#### V3 block size

Block size is chosen per file based on uncompressed file size:

| File size     | Block size |
|---------------|------------|
| ≤ 2 MB        | 64 KB      |
| > 2 MB        | 256 KB     |

The chosen block size is stored in the `block_size` field of the entry header so the decompressor does not need to infer it.

#### V3 TOC

Written at `toc_offset` after all entry data:

```
[4]  entry_count   uint32 LE  — mirrors header entry_count
[4]  reserved      uint32 LE  — zero
Per entry (entry_count entries):
  [4]  data_offset   uint32 LE  — byte offset of this entry from start of file
  [4]  path_crc32    uint32 LE  — CRC32 of the ASCII path string
```

The TOC enables random access to any entry by path CRC without scanning the full data stream.

#### V3 codec contest

For every block the packer runs all codecs independently on the raw block bytes and keeps the smallest result. A codec result is used only if it is smaller than `block_size × 0.98` (2% minimum improvement threshold). Otherwise the block is stored. Already-incompressible content (detected by entropy analysis of the file header bytes) bypasses the contest entirely and is stored raw.

#### V3 pre-filter selection

Before blocking, the packer analyses the file and selects the filter most likely to improve compression:

- Files with x86 branch density above threshold → x86 filter
- Files with low raw entropy and strong delta improvement → delta filter (8, 16, or 32-bit, whichever gives best estimated gain)
- Files with stereo PCM characteristics → stereo delta filter
- Otherwise → no filter

The packer compares estimated compressed sizes with and without the filter and only applies it if the filtered version is predicted to be smaller.

#### V3 NuGet dependency

V3 Zstd support requires the `ZstdSharp.Port` NuGet package:

```xml
<PackageReference Include="ZstdSharp.Port" Version="0.8.7" />
```

---

## Compression Selection Summary

| Version | Block size | Codecs available                                        |
|---------|------------|---------------------------------------------------------|
| V1      | Whole file (≤ 64 KB) | LZ77, LZSS (stored if > 64 KB)          |
| V2      | 64 KB fixed | Stored, LZ77, LZSS, RLE, LZ77+Huffman, LZSS+Huffman  |
| V3      | 64 KB / 256 KB (variable) | Stored, LZ77, LZSS, Deflate, Strided Deflate, Brotli, Zstd |

---

## Limitations

| Limitation        | Detail                                                                  |
|-------------------|-------------------------------------------------------------------------|
| Path length       | Maximum 255 bytes per path entry                                        |
| Archive entries   | No hard limit in the format; packer holds entry metadata in memory      |
| File size (V1)    | Files compressing to > 65,536 bytes are stored uncompressed             |
| File size (V2/V3) | No effective limit; files split into fixed-size blocks                  |
| Compression (V1)  | Single-pass; no multi-block or Huffman coding                           |
| x86 filter (V2)   | Not emitted by current packer; `file_flag = 0x02` is reserved          |
| V3 directories    | No directory entries in stream; hierarchy encoded in path strings only  |
| Paths             | ASCII only; no Unicode support                                          |
| Encoding          | No encryption, no digital signatures                                    |
| Xbox extractor    | V3 Brotli/Zstd blocks require a future Xbox-side decoder implementation |

---

## Building

The tool targets **.NET 8** with WPF. Open the solution in Visual Studio 2022 or later and build normally.

**NuGet dependencies:**

| Package            | Version | Required for        |
|--------------------|---------|---------------------|
| `ZstdSharp.Port`   | 0.8.7   | V3 Zstd compression |

---

## License

Part of the [XbDiag](https://github.com/Team-Resurgent/XbDiag) project.  
© Team Resurgent / Darkone83. See repository root for license details.

<div align-center>
  <img src="https://github.com/Darkone83/XBA-Archive-Tool/blob/main/img/xba.png" width=250> <img src="https://github.com/Darkone83/XBA-Archive-Tool/blob/main/img/Darkone83.png" width=400>
</div>

# XBA Archive Tool

A Windows WPF tool for creating, inspecting, and extracting `.xba` archives — a custom compressed archive format designed for deployment on original Xbox hardware via the [XbDiag](https://github.com/Team-Resurgent/XbDiag) diagnostic suite.

Developed by **Darkone83**.

---

## Features

- Pack a directory tree into a V1 or V2 `.xba` archive
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
4. Select the archive version using the **V1 / V2 toggle** in the toolbar
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
| Type       | `DIR v1`, `FILE v1`, `DIR v2`, or `FILE v2`             |
| Filter     | `x86` if the x86 branch filter was applied (V1 only)    |
| Blocks     | Number of compressed blocks (V2 files only)             |
| Size       | Uncompressed file size                                   |
| Compressed | Compressed size stored in the archive                   |
| Ratio      | Compressed / uncompressed, or `stored` if no compression|
| CRC32      | CRC32 of the original uncompressed data                 |

---

## Archive Format

XBA uses a magic-versioned sequential container. All multi-byte integers are **little-endian**. Paths use **backslash separators**, are **ASCII-encoded**, and are **relative with no leading backslash**.

### Magic bytes

| Version | Magic (4 bytes hex) |
|---------|---------------------|
| V1      | `58 42 41 01` (`XBA\x01`) |
| V2      | `58 42 41 02` (`XBA\x02`) |

### Common header

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

| Value | Meaning                          |
|-------|----------------------------------|
| `0x00`| Plain file, LZ77 or stored       |
| `0x01`| Directory entry                  |
| `0x02`| x86-filtered file, LZ77          |
| `0x03`| Plain file, LZSS                 |
| `0x04`| x86-filtered file, LZSS          |

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

| Value  | Meaning                                        |
|--------|------------------------------------------------|
| `0x00` | Plain file, no filter                          |
| `0x01` | Directory entry                                |
| `0x02` | x86-filtered file *(reserved; see note below)*|

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
[4]  lz_size         uint32 LE — byte count of the LZ-compressed intermediate
[256] code_lengths   one byte per symbol 0–255; 0 = symbol absent from tree
[...] bitstream      remainder of comp_size bytes, packed LSB-first
```

1. The bitstream is Huffman-decoded using canonical codes rebuilt from `code_lengths`, producing `lz_size` bytes of LZ data.
2. That LZ data is then decompressed with LZ77 (flag `0x04`) or LZSS (flag `0x05`) to produce the final block output.

**Canonical Huffman code assignment:**  
Symbols sorted by (code_length ascending, symbol value ascending). Codes assigned sequentially per length level, left-shifted when length increases. Lookup table is a flat 32,768-entry (2^15) direct-index table keyed on the next 15 bits of the bitstream LSB-first.

#### V2 CRC32

Computed on the **final decoded data** (all blocks reassembled in order, after any filter reversal). Uses the standard IEEE 802.3 polynomial (`0xEDB88320` reflected).

---

## Compression Selection (V2 Packer)

For each 65,536-byte block the packer tries all applicable codecs and selects the smallest result:

1. Stored (baseline)
2. LZ77
3. LZSS
4. RLE
5. LZ77 + Huffman
6. LZSS + Huffman

A codec result is used only if it is smaller than `block_size × 0.98` (2% minimum improvement). Otherwise the block is stored.

---

## Limitations

| Limitation | Detail |
|---|---|
| Path length | Maximum 255 bytes per path entry |
| Archive entries | No hard limit in the format; packer holds all entries in memory |
| File size (V1) | Files compressing to > 65,536 bytes are stored uncompressed |
| File size (V2) | No effective limit; files split into 65,536-byte blocks |
| Compression (V1) | Single-pass; no multi-block or Huffman coding |
| x86 filter (V2) | Not emitted by current packer; `file_flag = 0x02` is reserved |
| Paths | ASCII only; no Unicode support |
| Encoding | No encryption, no digital signatures |
| Concurrency | Single-threaded packer; large archives may take time |
| Xbox extractor | Static 65,536-byte buffers; V2 blocks > 65,536 bytes will fail |

---

## Building

The tool targets **.NET 8** with WPF. Open the solution in Visual Studio 2022 or later and build normally. No external NuGet dependencies.

---

## License

Part of the [XbDiag](https://github.com/Team-Resurgent/XbDiag) project.  
© Team Resurgent / Darkone83. See repository root for license details.

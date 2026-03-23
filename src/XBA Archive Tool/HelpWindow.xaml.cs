// HelpWindow.xaml.cs

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace XbaTool
{
    public partial class HelpWindow : Window
    {
        // ── Construction ─────────────────────────────────────────────────

        public HelpWindow()
        {
            InitializeComponent();
            TopicList.SelectedIndex = 0;
        }

        // ── Input ────────────────────────────────────────────────────────

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
        }

        // ── Topic selection ──────────────────────────────────────────────

        private void TopicList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TopicList.SelectedItem is not ListBoxItem item) return;
            string topic = item.Content?.ToString() ?? "";
            TxtTopicTitle.Text = topic;
            ContentPanel.Children.Clear();
            BuildTopic(topic);
        }

        // ── Topic builder ─────────────────────────────────────────────────

        private void BuildTopic(string topic)
        {
            switch (topic)
            {
                case "Getting Started": BuildGettingStarted(); break;
                case "The Toolbar": BuildToolbar(); break;
                case "The File List": BuildFileList(); break;
                case "Pack": BuildPack(); break;
                case "Unpack": BuildUnpack(); break;
                case "Test": BuildTest(); break;
                case "Format: V1": BuildFormatV1(); break;
                case "Format: V2": BuildFormatV2(); break;
                case "Format: V3": BuildFormatV3(); break;
                case "Errors & Fixes": BuildErrors(); break;
            }
        }

        // ── Content helpers ───────────────────────────────────────────────

        private void H2(string text)
        {
            ContentPanel.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = Brush("BrAccentHot"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 18, 0, 6)
            });
        }

        private void Para(string text)
        {
            ContentPanel.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = Brush("BrFg"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                LineHeight = 20
            });
        }

        private void Bullet(string label, string body)
        {
            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 2)
            };

            sp.Children.Add(new TextBlock
            {
                Text = "▸  ",
                Foreground = Brush("BrAccent"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8, 0, 0, 0)
            });

            var tb = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 0)
            };

            if (!string.IsNullOrEmpty(label))
            {
                tb.Inlines.Add(new Run(label + "  ")
                {
                    Foreground = Brush("BrFg"),
                    FontWeight = FontWeights.SemiBold
                });
            }
            tb.Inlines.Add(new Run(body) { Foreground = Brush("BrFg") });

            var wrap = new Grid { Margin = new Thickness(0, 0, 24, 0) };
            wrap.Children.Add(tb);
            sp.Children.Add(wrap);
            ContentPanel.Children.Add(sp);
        }

        private void Note(string text)
        {
            ContentPanel.Children.Add(new Border
            {
                Background = Brush("BrBg3"),
                BorderBrush = Brush("BrAccent"),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 8, 0, 8),
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = Brush("BrFgDim"),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 18
                }
            });
        }

        private void Sep()
        {
            ContentPanel.Children.Add(new Border
            {
                Height = 1,
                Background = Brush("BrBg3"),
                Margin = new Thickness(0, 10, 0, 10)
            });
        }

        private SolidColorBrush Brush(string key) =>
            (SolidColorBrush)Application.Current.Resources[key];

        // ── Topics ────────────────────────────────────────────────────────

        private void BuildGettingStarted()
        {
            Para("XBA Archive Tool packs and unpacks .xba archives — a compact binary " +
                 "container format designed for original Xbox content distribution.");

            H2("Opening an archive");
            Para("Drag and drop any .xba file onto the window, or click Open in the toolbar " +
                 "to browse for one. The file list populates immediately and a background " +
                 "integrity check runs automatically.");

            H2("Creating an archive");
            Para("Click Pack, choose the source folder, then choose a destination filename. " +
                 "Select the format version from the dropdown before packing — V3 is " +
                 "recommended for new archives.");

            H2("Extracting files");
            Para("With an archive open, click Unpack (or right-click the file list) and " +
                 "choose a destination folder. Selected files only are extracted if you have " +
                 "a selection; otherwise the full archive is unpacked.");

            Note("Tip: press F1 at any time to open this help window.");
        }

        private void BuildToolbar()
        {
            H2("Open");
            Para("Opens a file browser to select an .xba archive. Drag-and-drop onto the " +
                 "window works as an alternative.");

            H2("Pack");
            Para("Prompts for a source folder and a destination .xba filename, then " +
                 "compresses the entire folder tree using the format selected in the version " +
                 "dropdown.");

            H2("Version dropdown  (V3 / V2 / V1)");
            Para("Selects the archive format used when packing. When you open an existing " +
                 "archive the dropdown automatically updates to reflect that archive's format. " +
                 "See the Format topics for a detailed comparison.");

            H2("Unpack");
            Para("Extracts the open archive (or the current selection) to a folder you choose. " +
                 "Enabled only when an archive is open.");

            H2("Test");
            Para("Decodes every block in the archive and verifies CRC32 checksums without " +
                 "writing any files to disk. Reports a pass/fail count on completion.");

            H2("About");
            Para("Shows version and credits information.");

            Sep();
            Note("Unpack and Test are greyed out until an archive is open.");
        }

        private void BuildFileList()
        {
            Para("The file list shows every entry in the open archive. Columns:");

            H2("Columns");
            Bullet("Name", "Relative path of the entry inside the archive.");
            Bullet("Type", "FILE or DIR, tagged with the format version (v1/v2/v3).");
            Bullet("Filter", "Pre-compression filter applied to the file: x86 (executable " +
                                 "filter), delta8/16/stereo16/delta32 (audio/data filters), " +
                                 "or blank for none.");
            Bullet("Blocks", "Number of compressed blocks the file is split into (V2/V3). " +
                                 "V1 files are always single-block.");
            Bullet("Size", "Original uncompressed file size.");
            Bullet("Compressed", "Total compressed size stored in the archive.");
            Bullet("Ratio", "Compressed ÷ original, as a percentage. Lower is better.");
            Bullet("CRC32", "Stored checksum of the original data, used by Test.");

            H2("Selection");
            Para("Click a row to select it; hold Ctrl or Shift for multi-select. " +
                 "Right-clicking any selection shows a context menu with Unpack and Test.");

            Note("Double-clicking a row currently has no action — extraction is via the " +
                 "Unpack button or context menu.");
        }

        private void BuildPack()
        {
            Para("Pack compresses an entire directory tree into a single .xba file.");

            H2("Workflow");
            Bullet("1.", "Select the target format version in the dropdown (V3 recommended).");
            Bullet("2.", "Click Pack.");
            Bullet("3.", "Choose the source folder in the first dialog.");
            Bullet("4.", "Choose the destination filename in the second dialog.");
            Bullet("5.", "A progress window tracks each file as it is compressed.");

            H2("What gets included");
            Para("Every file and subdirectory under the source folder is included recursively. " +
                 "Paths stored in the archive are relative to the source folder root, using " +
                 "backslash separators.");

            H2("Compression behaviour");
            Para("V3 analyses each file's entropy and content type before choosing a " +
                 "pre-filter and block codec automatically. Files that are already compressed " +
                 "(ZIP, PNG, MP3, XMV, etc.) are stored without re-compression.");

            Note("Packing can be cancelled at any time using the Cancel button in the " +
                 "progress window.");
        }

        private void BuildUnpack()
        {
            Para("Unpack extracts files from the open archive to a folder on disk.");

            H2("Workflow");
            Bullet("1.", "Open an archive.");
            Bullet("2.", "Optionally select specific files in the list (Ctrl/Shift+click). " +
                         "If nothing is selected, the full archive is unpacked.");
            Bullet("3.", "Click Unpack or use the right-click context menu.");
            Bullet("4.", "Choose a destination folder.");
            Bullet("5.", "Files are written preserving their relative paths.");

            H2("Overwrite behaviour");
            Para("Existing files at the destination are overwritten without prompting. " +
                 "Ensure the destination is correct before confirming.");

            Note("Subdirectories are created automatically as needed.");
        }

        private void BuildTest()
        {
            Para("Test verifies archive integrity by fully decoding every compressed block " +
                 "and comparing the result against the stored CRC32 checksum. No files are " +
                 "written to disk.");

            H2("When to use it");
            Bullet("", "After packing, to confirm the output is valid.");
            Bullet("", "Before unpacking an archive received from another machine.");
            Bullet("", "To diagnose a suspected corrupt download.");

            H2("Results");
            Para("The status bar shows the number of files that passed and failed. A " +
                 "message box appears if any CRC errors are found, listing the count.");

            Note("Test is also run automatically in the background whenever you open an " +
                 "archive. The status bar shows the result once it completes.");
        }

        private void BuildFormatV1()
        {
            Para("V1 is the original XBA format. It is simple and broadly compatible but " +
                 "has the fewest compression options.");

            H2("Header");
            Para("4-byte magic (XBA\\x01) followed by a 32-bit entry count.");

            H2("Per-entry layout");
            Para("Each entry stores a flags byte, a path length byte, the ASCII path, and " +
                 "for files: uncompressed size, compressed size, CRC32, then the raw " +
                 "compressed data as a single block.");

            H2("Compression flags");
            Bullet("0x00", "File — LZ77 or stored.");
            Bullet("0x01", "Directory.");
            Bullet("0x02", "File — x86 pre-filter + LZ77 or stored.");
            Bullet("0x03", "File — LZSS or stored.");
            Bullet("0x04", "File — x86 pre-filter + LZSS or stored.");

            Note("If compressed size equals uncompressed size the data is stored as-is " +
                 "(no compression). CRC32 is of the original, unfiltered data.");
        }

        private void BuildFormatV2()
        {
            Para("V2 adds multi-block compression and a richer block codec selection, " +
                 "improving ratios on large files.");

            H2("Header");
            Para("4-byte magic (XBA\\x02) followed by a 32-bit entry count.");

            H2("Per-entry layout");
            Para("Flags byte, path length, ASCII path, then for files: uncompressed size, " +
                 "CRC32, a 16-bit block count, then one or more blocks. Each block carries " +
                 "its own flag byte and compressed size.");

            H2("File flags");
            Bullet("0x00", "File — no pre-filter.");
            Bullet("0x01", "Directory.");
            Bullet("0x02", "File — x86 filter applied to the whole file before blocking.");

            H2("Block codecs");
            Bullet("0x00", "Stored (no compression).");
            Bullet("0x01", "LZ77.");
            Bullet("0x02", "LZSS.");
            Bullet("0x03", "RLE.");
            Bullet("0x04", "LZ77 + Huffman.");
            Bullet("0x05", "LZSS + Huffman.");

            Note("Each block decompresses to 65536 bytes except the last. CRC32 covers " +
                 "the fully reassembled, unfiltered file.");
        }

        private void BuildFormatV3()
        {
            Para("V3 is the current recommended format. It adds an indexed TOC, whole-file " +
                 "pre-filters, variable block sizes, and a full modern codec contest per block " +
                 "(Deflate, strided Deflate, Brotli, Zstd). Files only — directory hierarchy " +
                 "is encoded entirely in path strings.");

            H2("Header  (16 bytes)");
            Bullet("magic[4]", "XBA\\x03");
            Bullet("entry_count[4]", "Number of file entries (directories are not stored).");
            Bullet("toc_offset[4]", "Byte offset of the TOC from the start of the file.");
            Bullet("flags[4]", "Bit 0 = has_solid_blocks (reserved for future use).");

            H2("Per-entry header");
            Bullet("filter_flag[1]", "Whole-file pre-filter applied before blocking (see below).");
            Bullet("path_len[1] + path[N]", "ASCII relative path.");
            Bullet("uncomp_size[4]", "Total uncompressed file size.");
            Bullet("crc32[4]", "CRC32 of the final decoded and unfiltered data.");
            Bullet("block_count[2]", "Number of compressed blocks.");
            Bullet("block_size[4]", "Decompressed size of each non-last block. Stored explicitly " +
                                    "so the decompressor never has to guess.");

            H2("Pre-filters");
            Bullet("0x00", "None.");
            Bullet("0x01", "x86 executable filter.");
            Bullet("0x02", "Delta8 — byte-level delta for 8-bit PCM or similar.");
            Bullet("0x03", "Delta16 — 16-bit delta.");
            Bullet("0x04", "Stereo16 — interleaved stereo delta.");
            Bullet("0x05", "Delta32 — 32-bit delta.");
            Bullet("0xFF", "Solid block marker (reserved).");

            H2("Block codecs");
            Bullet("0x00", "Stored (no compression).");
            Bullet("0x01", "LZ77.");
            Bullet("0x02", "LZSS.");
            Bullet("0x03", "Raw DEFLATE (RFC 1951, no zlib wrapper).");
            Bullet("0x04", "Strided DEFLATE — stride byte + DEFLATE of stride-split data. " +
                           "Effective on structured numeric data, vectors, PCM audio.");
            Bullet("0x05", "Brotli (RFC 7932) — quality 7, window 22. Built-in .NET, no " +
                           "external dependency. Strong on text, fonts, and mixed content.");
            Bullet("0x06", "Zstd (RFC 8878) — level 15, standard zstd frame. Pure managed " +
                           "C# via ZstdSharp.Port. Best general-purpose binary codec.");

            H2("Variable block size");
            Para("Block size is chosen per file: files ≤ 2 MB use 64 KB blocks for full " +
                 "codec window coverage; files > 2 MB use 256 KB blocks. The chosen size " +
                 "is stored in the block_size field of the entry header.");

            H2("Codec contest");
            Para("For every block all codecs compete independently on the raw block bytes. " +
                 "The smallest result wins. A codec must beat the uncompressed size by at " +
                 "least 2% to be selected — otherwise the block is stored raw.");

            H2("TOC");
            Para("A random-access table at toc_offset giving the byte offset and path CRC32 " +
                 "of each entry, enabling fast single-file extraction without scanning the " +
                 "whole archive.");

            Note("Pre-filters are applied to the whole file before blocking and reversed " +
                 "after all blocks are reassembled. CRC32 covers the final unfiltered data.");
        }

        private void BuildErrors()
        {
            H2("\"Not a valid XBA archive\"");
            Para("The file's first 4 bytes do not match any known XBA magic signature. " +
                 "Possible causes:");
            Bullet("", "The file is not an XBA archive (wrong format or wrong file chosen).");
            Bullet("", "The file is truncated or the header was overwritten.");
            Bullet("", "The file was renamed with a .xba extension but is actually something else.");

            Sep();

            H2("\"Cannot open archive\"  /  file open error");
            Para("The archive could not be read from disk. Check:");
            Bullet("", "The file still exists at the path shown.");
            Bullet("", "You have read permission on the file.");
            Bullet("", "The file is not locked by another process.");

            Sep();

            H2("CRC errors reported by Test");
            Para("One or more files failed checksum verification. Possible causes:");
            Bullet("", "The archive was corrupted in transit (partial download, bad sector).");
            Bullet("", "The file was modified after packing using an external tool.");
            Bullet("", "A bug in an older version of the packer wrote an incorrect CRC.");
            Para("Re-download or re-pack the archive from the original source if available.");

            Sep();

            H2("Unpack produces empty or missing files");
            Para("If Test passes but unpacked files are empty, check that the destination " +
                 "drive has sufficient free space and that you have write permission on the " +
                 "output folder.");

            Sep();

            H2("Progress window appears to hang");
            Para("Very large files with high-entropy content (already-compressed data that " +
                 "slipped through the magic-byte check) can take longer than expected. " +
                 "Use the Cancel button to abort and inspect the source files.");

            Note("If you encounter a repeatable crash or a file that cannot be packed or " +
                 "unpacked correctly, note the archive version and file size and report it " +
                 "to the team.");
        }
    }
}
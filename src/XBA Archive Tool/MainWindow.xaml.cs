// MainWindow.xaml.cs

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace XbaTool
{
    // =========================================================================
    // ViewModel for a single file-list row
    // =========================================================================

    public class EntryViewModel : INotifyPropertyChanged
    {
        public ArchiveEntry Entry { get; }

        public EntryViewModel(ArchiveEntry e) { Entry = e; }

        public string Path => Entry.Path;
        public string TypeLabel => Entry.Type == EntryType.Directory
            ? (Entry.IsV2 ? "DIR v2" : "DIR v1")
            : (Entry.IsV2 ? "FILE v2" : "FILE v1");

        // Filter / codec summary shown in the Filter column.
        // v1: reflects per-file codec flag.
        // v2: reflects x86 filter flag; block-level codecs shown via BlocksLabel.
        public string FilterLabel => Entry.IsV2
            ? (Entry.X86Filter ? "x86" : "")
            : (Entry.X86Filter && Entry.UsesLzss ? "x86+lzss" :
               Entry.X86Filter ? "x86+lz77" :
               Entry.UsesLzss ? "lzss" : "lz77");

        // v2 only: shows block count (e.g. "4 blk").
        public string BlocksLabel => Entry.IsV2 && Entry.Type == EntryType.File
            ? $"{Entry.BlockCount} blk"
            : "";

        public string SizeLabel => Entry.Type == EntryType.Directory ? ""
                                     : FmtSize(Entry.UncompSize);
        public string CompLabel => Entry.Type == EntryType.Directory ? ""
                                     : FmtSize(Entry.CompSize);
        public string RatioLabel => Entry.Type == EntryType.Directory ? ""
                                     : Entry.Stored ? "stored"
                                     : $"{Entry.Ratio:F0}%";
        public string CrcLabel => Entry.Type == EntryType.Directory ? ""
                                     : $"{Entry.Crc32:X8}";

        // Colour for the row.
        // v2 x86 files get BrFilter; v2 non-x86 compressed get BrComp.
        // v1 follows same logic as before.
        public Brush RowBrush =>
            Entry.Type == EntryType.Directory
                ? (Brush)Application.Current.Resources["BrDir"]
            : Entry.X86Filter && !Entry.Stored
                ? (Brush)Application.Current.Resources["BrFilter"]
            : Entry.Stored
                ? (Brush)Application.Current.Resources["BrStore"]
                : (Brush)Application.Current.Resources["BrComp"];

        private static string FmtSize(long n)
        {
            if (n < 1024) return $"{n} B";
            if (n < 1024 * 1024) return $"{n / 1024.0:F1} KB";
            if (n < 1024L * 1024 * 1024) return $"{n / 1024.0 / 1024.0:F2} MB";
            return $"{n / 1024.0 / 1024.0 / 1024.0:F2} GB";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // =========================================================================
    // Progress popup window
    // =========================================================================

    public class ProgressWindow : Window
    {
        private readonly TextBlock _lblFile;
        private readonly TextBlock _lblCount;
        private readonly ProgressBar _bar;
        private readonly Button _btnCancel;
        private readonly CancelEventHandler _closingGuard;
        public CancellationTokenSource Cts { get; } = new();

        public ProgressWindow(string title, Window owner)
        {
            Title = title;
            Owner = owner;
            Width = 480;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var panel = new StackPanel { Margin = new Thickness(16) };

            _lblFile = new TextBlock
            {
                Text = "",
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 6)
            };

            _lblCount = new TextBlock
            {
                Text = "",
                Margin = new Thickness(0, 0, 0, 6)
            };

            _bar = new ProgressBar
            {
                Height = 18,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Margin = new Thickness(0, 0, 0, 10)
            };

            _btnCancel = new Button
            {
                Content = "Cancel",
                Width = 80,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _btnCancel.Click += (_, _) =>
            {
                Cts.Cancel();
                _btnCancel.IsEnabled = false;
                _lblFile.Text = "Cancelling…";
            };

            panel.Children.Add(_lblFile);
            panel.Children.Add(_lblCount);
            panel.Children.Add(_bar);
            panel.Children.Add(_btnCancel);
            Content = panel;

            // Guard: if the user hits X while the operation is running, cancel
            // instead of closing.  Once AllowClose() is called this guard is
            // removed so the window closes normally.
            _closingGuard = (_, e) =>
            {
                if (!Cts.IsCancellationRequested)
                {
                    Cts.Cancel();
                    _btnCancel.IsEnabled = false;
                    _lblFile.Text = "Cancelling…";
                }
                // Always block the close — the caller's finally block will call
                // AllowClose() + Close() once the background task has stopped.
                e.Cancel = true;
            };
            Closing += _closingGuard;
        }

        public void Update(int done, int total, string file)
        {
            _lblFile.Text = file;
            _lblCount.Text = $"{done} / {total}";
            _bar.Value = total > 0 ? done * 100.0 / total : 0;
        }

        // Call this before Close() to remove the guard so the window can
        // actually close.
        public void AllowClose()
        {
            Closing -= _closingGuard;
            _btnCancel.IsEnabled = false;
        }
    }

    // =========================================================================
    // MainWindow
    // =========================================================================

    public partial class MainWindow : Window
    {
        private string? _archivePath;
        private CancellationTokenSource? _cts;
        private readonly ObservableCollection<EntryViewModel> _items = new();

        public MainWindow()
        {
            InitializeComponent();
            FileList.ItemsSource = _items;
            FileList.AddHandler(
                GridViewColumnHeader.ClickEvent,
                new RoutedEventHandler(ColumnHeader_Click));
            ApplyRowColours();
        }

        // ── Apply foreground colours to list items ────────────────────────

        private void ApplyRowColours()
        {
            FileList.ItemContainerGenerator.StatusChanged += (s, e) =>
            {
                if (FileList.ItemContainerGenerator.Status ==
                    System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                {
                    foreach (var vm in _items)
                    {
                        if (FileList.ItemContainerGenerator.ContainerFromItem(vm)
                            is ListViewItem lvi)
                            lvi.Foreground = vm.RowBrush;
                    }
                }
            };
        }

        // ── Populate list ─────────────────────────────────────────────────

        private void PopulateList(List<ArchiveEntry> entries)
        {
            _items.Clear();
            long tu = 0, tc = 0;
            int files = 0, dirs = 0;

            foreach (var e in entries)
            {
                var vm = new EntryViewModel(e);
                _items.Add(vm);
                if (e.Type == EntryType.Directory) dirs++;
                else { files++; tu += e.UncompSize; tc += e.CompSize; }
            }

            Dispatcher.BeginInvoke(() =>
            {
                foreach (var vm in _items)
                {
                    if (FileList.ItemContainerGenerator.ContainerFromItem(vm)
                        is ListViewItem lvi)
                        lvi.Foreground = vm.RowBrush;
                }
            }, System.Windows.Threading.DispatcherPriority.Loaded);

            string ratio = tu > 0 ? $"{(double)tc / tu * 100:F1}%" : "n/a";
            TxtCount.Text =
                $"{files} file(s)  {dirs} dir(s)  |  " +
                $"{FmtSize(tu)} → {FmtSize(tc)}  [{ratio}]";
        }

        // ── Sort ──────────────────────────────────────────────────────────

        private string _sortCol = "";
        private bool _sortAsc = true;
        private readonly Dictionary<string, string> _colProps = new()
        {
            ["Name"] = nameof(EntryViewModel.Path),
            ["Type"] = nameof(EntryViewModel.TypeLabel),
            ["Filter"] = nameof(EntryViewModel.FilterLabel),
            ["Blocks"] = nameof(EntryViewModel.BlocksLabel),
            ["Size"] = nameof(EntryViewModel.SizeLabel),
            ["Compressed"] = nameof(EntryViewModel.CompLabel),
            ["Ratio"] = nameof(EntryViewModel.RatioLabel),
            ["CRC32"] = nameof(EntryViewModel.CrcLabel),
        };

        private void ColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not GridViewColumnHeader h || h.Column == null)
                return;
            string col = h.Column.Header?.ToString() ?? "";
            if (!_colProps.TryGetValue(col, out var prop)) return;
            if (_sortCol == col) _sortAsc = !_sortAsc;
            else { _sortCol = col; _sortAsc = true; }

            var view = CollectionViewSource.GetDefaultView(FileList.ItemsSource);
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(prop,
                _sortAsc ? ListSortDirection.Ascending : ListSortDirection.Descending));
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private static string FmtSize(long n)
        {
            if (n < 1024) return $"{n} B";
            if (n < 1024 * 1024) return $"{n / 1024.0:F1} KB";
            if (n < 1024L * 1024 * 1024) return $"{n / 1024.0 / 1024.0:F2} MB";
            return $"{n / 1024.0 / 1024.0 / 1024.0:F2} GB";
        }

        private void SetStatus(string msg) => TxtStatus.Text = msg;
        private void SetProgress(int done, int total)
        {
            ProgressBar.Visibility = Visibility.Visible;
            ProgressBar.Value = total > 0 ? done * 100.0 / total : 0;
        }
        private void ClearProgress()
        {
            ProgressBar.Value = 0;
            ProgressBar.Visibility = Visibility.Collapsed;
        }

        private void SetArchiveButtons(bool enabled)
        {
            BtnUnpack.IsEnabled = enabled;
            BtnTest.IsEnabled = enabled;
        }

        private bool IsBusy() => _cts != null && !_cts.IsCancellationRequested;

        // ── Open ──────────────────────────────────────────────────────────

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "XBA archives (*.xba)|*.xba|All files (*.*)|*.*",
                Title = "Open XBA Archive"
            };
            if (dlg.ShowDialog() == true)
                OpenArchive(dlg.FileName);
        }

        private async void OpenArchive(string path)
        {
            // ── Quick structural validation ───────────────────────────────
            // List() validates magic and reads the entry table; if it throws
            // the archive is invalid or corrupt before we even try to decode.
            List<ArchiveEntry> entries;
            try
            {
                entries = XbaCodec.List(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Cannot open archive:\n{ex.Message}",
                    "Invalid Archive", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _archivePath = path;
            TxtPath.Text = path;
            PopulateList(entries);
            SetArchiveButtons(true);
            SetStatus($"Opened: {System.IO.Path.GetFileName(path)}  —  {entries.Count} entries");

            // ── Background integrity check (CRC decode of every block) ────
            int fileCount = entries.Count(e => e.Type == EntryType.File);
            if (fileCount == 0) return;

            SetStatus($"Verifying {fileCount} file(s)…");
            SetProgress(0, 1);

            var pw = new ProgressWindow($"Verifying — {System.IO.Path.GetFileName(path)}", this);
            pw.Show();

            var progressRpt = new Progress<ProgressReport>(r =>
            {
                pw.Update(r.Done, r.Total, r.CurrentFile);
                SetProgress(r.Done, r.Total);
            });

            TestResult result;
            var sw = Stopwatch.StartNew();
            try
            {
                result = await Task.Run(() =>
                    XbaCodec.Test(path, progressRpt, pw.Cts.Token));
                sw.Stop();
            }
            catch (OperationCanceledException)
            {
                pw.AllowClose(); pw.Close(); ClearProgress();
                SetStatus("Verification cancelled.");
                return;
            }
            catch (Exception ex)
            {
                pw.AllowClose(); pw.Close(); ClearProgress();
                SetStatus("Verification error.");
                MessageBox.Show(
                    $"Archive appears corrupt:\n{ex.Message}",
                    "Corrupt Archive", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            pw.AllowClose(); pw.Close(); ClearProgress();

            if (result.Errors > 0)
            {
                SetStatus($"⚠ {result.Errors} corrupt file(s) detected  —  {sw.Elapsed.TotalSeconds:F1}s");
                MessageBox.Show(
                    $"{result.Errors} file(s) failed CRC verification.\n" +
                    $"The archive may be corrupt or was packed with an incompatible version.",
                    "Corrupt Archive", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                SetStatus($"OK — {result.Ok} file(s) verified  —  {sw.Elapsed.TotalSeconds:F1}s");
            }
        }

        // ── Pack ──────────────────────────────────────────────────────────

        private async void BtnPack_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy()) return;

            var srcDlg = new OpenFolderDialog { Title = "Select directory to pack" };
            if (srcDlg.ShowDialog() != true) return;
            string src = srcDlg.FolderName;

            var dstDlg = new SaveFileDialog
            {
                FileName = System.IO.Path.GetFileName(src) + ".xba",
                DefaultExt = ".xba",
                Filter = "XBA archives (*.xba)|*.xba|All files (*.*)|*.*",
                Title = "Save XBA Archive As"
            };
            if (dstDlg.ShowDialog() != true) return;
            string dst = dstDlg.FileName;

            var pw = new ProgressWindow($"Packing — {System.IO.Path.GetFileName(dst)}", this);
            pw.Show();
            _cts = pw.Cts;

            SetStatus("Packing…");
            SetProgress(0, 1);
            var sw = Stopwatch.StartNew();

            var progressRpt = new Progress<ProgressReport>(r =>
            {
                pw.Update(r.Done, r.Total, r.CurrentFile);
                SetProgress(r.Done, r.Total);
                SetStatus($"Packing  [{r.Done}/{r.Total}]  {r.CurrentFile}");
            });

            var packDebug = new System.Collections.Generic.List<string>();
            var logRpt = new Progress<LogEntry>(l =>
            {
                if (l.Kind == "debug")
                    packDebug.Add($"{l.FilePath}\n  original CRC: {l.ExpectedCrc:X8}" +
                                  $"  filtered CRC: {l.FilteredCrc:X8}");
            });

            try
            {
                bool useV1 = RbV1.IsChecked == true;
                if (useV1)
                    await Task.Run(() => XbaCodec.PackV1(src, dst, progressRpt, logRpt, _cts.Token));
                else
                    await Task.Run(() => XbaCodec.Pack(src, dst, progressRpt, logRpt, _cts.Token));
                sw.Stop();
                pw.AllowClose(); pw.Close();

                var entries = XbaCodec.List(dst);
                _archivePath = dst;
                TxtPath.Text = dst;
                PopulateList(entries);
                SetArchiveButtons(true);

                long inBytes = entries.Where(e => e.Type == EntryType.File).Sum(e => e.UncompSize);
                long outBytes = new FileInfo(dst).Length;
                double ratio = inBytes > 0 ? (double)outBytes / inBytes * 100 : 100;
                string fmt = (RbV1.IsChecked == true) ? "V1" : "V2";
                SetStatus($"Packed [{fmt}] in {sw.Elapsed.TotalSeconds:F1}s  |  " +
                          $"{FmtSize(inBytes)} → {FmtSize(outBytes)}  [{ratio:F1}%]");

                if (packDebug.Count > 0)
                    MessageBox.Show(string.Join("\n\n", packDebug),
                        "Pack Debug — x86 filter CRCs",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                pw.AllowClose(); pw.Close();
                SetStatus("Pack cancelled.");
            }
            catch (Exception ex)
            {
                pw.AllowClose(); pw.Close();
                MessageBox.Show(ex.Message, "Pack Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("Pack failed.");
            }
            finally { ClearProgress(); _cts = null; }
        }

        // ── Unpack ────────────────────────────────────────────────────────

        private async void BtnUnpack_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy() || _archivePath == null) return;

            var dlg = new OpenFolderDialog { Title = "Select destination directory" };
            if (dlg.ShowDialog() != true) return;
            string dst = dlg.FolderName;

            var pw = new ProgressWindow(
                $"Unpacking — {System.IO.Path.GetFileName(_archivePath)}", this);
            pw.Show();
            _cts = pw.Cts;

            SetStatus("Unpacking…");
            SetProgress(0, 1);
            var sw = Stopwatch.StartNew();

            var progressRpt = new Progress<ProgressReport>(r =>
            {
                pw.Update(r.Done, r.Total, r.CurrentFile);
                SetProgress(r.Done, r.Total);
                SetStatus($"Unpacking  [{r.Done}/{r.Total}]  {r.CurrentFile}");
            });

            int files = 0, errors = 0;
            var crcErrors = new System.Collections.Generic.List<string>();
            var logRpt = new Progress<LogEntry>(l =>
            {
                if (l.Kind == "file") files++;
                if (l.Kind == "error")
                {
                    errors++;
                    string line = $"{l.FilePath}\n  expected {l.ExpectedCrc:X8}  got {l.ActualCrc:X8}";
                    if (l.FilteredCrc != 0)
                        line += $"\n  pre-unfilter assembled CRC: {l.FilteredCrc:X8}";
                    crcErrors.Add(line);
                }
            });

            string archPath = _archivePath;
            try
            {
                await Task.Run(() =>
                    XbaCodec.Unpack(archPath, dst, progressRpt, logRpt, _cts.Token));
                sw.Stop();
                pw.AllowClose(); pw.Close();

                string msg = $"Unpacked {files} files in {sw.Elapsed.TotalSeconds:F1}s";
                if (errors > 0) msg += $"  |  ⚠ {errors} CRC error(s)";
                SetStatus(msg);

                if (errors > 0)
                {
                    string detail = string.Join("\n\n", crcErrors);
                    MessageBox.Show(
                        $"{errors} file(s) failed CRC verification:\n\n{detail}",
                        "CRC Errors", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                    MessageBox.Show($"Unpacked to:\n{dst}", "Done",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                pw.AllowClose(); pw.Close();
                SetStatus("Unpack cancelled.");
            }
            catch (Exception ex)
            {
                pw.AllowClose(); pw.Close();
                MessageBox.Show(ex.Message, "Unpack Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("Unpack failed.");
            }
            finally { ClearProgress(); _cts = null; }
        }

        // ── Test ──────────────────────────────────────────────────────────

        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            if (IsBusy() || _archivePath == null) return;

            var pw = new ProgressWindow(
                $"Testing — {System.IO.Path.GetFileName(_archivePath)}", this);
            pw.Show();
            _cts = pw.Cts;

            SetStatus("Testing archive integrity…");
            SetProgress(0, 1);
            var sw = Stopwatch.StartNew();

            var progressRpt = new Progress<ProgressReport>(r =>
            {
                pw.Update(r.Done, r.Total, r.CurrentFile);
                SetProgress(r.Done, r.Total);
                SetStatus($"Testing  [{r.Done}/{r.Total}]  {r.CurrentFile}");
            });

            string archPath = _archivePath;
            try
            {
                TestResult testRes = await Task.Run(() =>
                    XbaCodec.Test(archPath, progressRpt, _cts.Token));
                sw.Stop();
                pw.AllowClose(); pw.Close();

                int ok = testRes.Ok, errs = testRes.Errors;
                if (errs == 0)
                {
                    SetStatus($"All {ok} files OK  —  {sw.Elapsed.TotalSeconds:F1}s");
                    MessageBox.Show($"All {ok} files passed CRC verification.",
                        "Test OK", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    SetStatus($"FAILED — {errs} CRC error(s)  —  {sw.Elapsed.TotalSeconds:F1}s");
                    MessageBox.Show($"{errs} file(s) failed CRC verification.",
                        "Test Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (OperationCanceledException)
            {
                pw.AllowClose(); pw.Close();
                SetStatus("Test cancelled.");
            }
            catch (Exception ex)
            {
                pw.AllowClose(); pw.Close();
                MessageBox.Show(ex.Message, "Test Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("Test failed.");
            }
            finally { ClearProgress(); _cts = null; }
        }

        // ── About ─────────────────────────────────────────────────────────

        private void BtnAbout_Click(object sender, RoutedEventArgs e)
        {
            var about = new AboutWindow { Owner = this };
            about.ShowDialog();
        }

        // ── FileList selection changed ────────────────────────────────────

        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Reserved for future per-selection status updates.
            // Currently a no-op — handler required by XAML binding.
        }

        // ── Double-click → unpack ─────────────────────────────────────────

        private void FileList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_archivePath != null)
                BtnUnpack_Click(sender, e);
        }

        // ── Drag and drop ─────────────────────────────────────────────────

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files &&
                files.Length > 0)
            {
                string f = files[0];
                if (System.IO.Path.GetExtension(f).Equals(".xba",
                    StringComparison.OrdinalIgnoreCase))
                    OpenArchive(f);
                else if (Directory.Exists(f))
                    PromptPackDir(f);
            }
        }

        private void PromptPackDir(string dir)
        {
            var result = MessageBox.Show(
                $"Pack directory:\n{dir}\n\nProceed?",
                "Pack Directory",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
                BtnPack_Click(this, new RoutedEventArgs());
        }
    }
}
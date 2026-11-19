using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Byterake.Core;
using Microsoft.Win32;

namespace Byterake.App;

public partial class MainWindow : Window
{
    readonly StartArgs _args;
    readonly ActivityLog _log;
    readonly bool _isAdmin;
    readonly Catalog _catalog;
    readonly List<TargetRow> _targets = new();
    readonly ConcurrentQueue<string> _pendingLog = new();
    readonly DispatcherTimer _logTimer;

    string _drive;
    ScanResult? _scan;
    long _goalBytes = 100 * Units.GB;
    long _threshold = 500 * Units.MB;
    const long ScanFloor = 100 * Units.MB; // scans collect files from this size so the threshold can change later

    CancellationTokenSource? _measureCts, _scanCts, _cleanCts;
    bool _busy;
    List<ProgramRow> _programs = new();
    string _sort = "size";
    bool _appsLoaded;

    public MainWindow(StartArgs args)
    {
        InitializeComponent();
        _args = args;
        _log = new ActivityLog();
        _isAdmin = SystemTools.IsAdministrator();
        _drive = args.Drive ?? (System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\");
        _catalog = Catalog.Load();

        _log.LineWritten += line => _pendingLog.Enqueue(line);
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _logTimer.Tick += (_, _) => FlushLog();
        _logTimer.Start();

        SourceInitialized += (_, _) => App.TitleBar(this);
        Loaded += (_, _) => Start();

        // Pages only scroll when the user scrolls or tabs to something, not when content changes size.
        foreach (var sv in new[] { PageOverview, PageReview })
            ((FrameworkElement)sv.Content).RequestBringIntoView += (s, e) =>
            {
                if (e.TargetObject is UIElement u && !u.IsKeyboardFocusWithin) e.Handled = true;
            };
    }

    // ------------------------------------------------------------------ start-up

    void Start()
    {
        _log.Info($"Byterake started with {(_isAdmin ? "administrator rights" : "standard rights")}. Catalog: {_catalog.Source} ({_catalog.Targets.Count} targets).");
        foreach (var w in _catalog.Warnings) _log.Info("Catalog: " + w);

        RightsText.Text = _isAdmin ? "Running as administrator" : "Running with standard rights";
        LogPathText.Text = _log.FilePath;
        while (_pendingLog.TryDequeue(out _)) { } // already included in Recent below
        LogBox.Text = string.Join(Environment.NewLine, _log.Recent) + Environment.NewLine;

        if (_catalog.Warnings.Count > 0)
        {
            CatalogWarnText.Text = "Catalog notes: " + string.Join("  ", _catalog.Warnings);
            CatalogWarnBox.Visibility = Visibility.Visible;
        }

        BuildDriveChips();
        RefreshDrive();
        BuildTargets();
        ScanInfoText.Text = "No scan yet. The catalog below is measured straight away; press Scan drive to see every folder.";
    }

    void BuildDriveChips()
    {
        DriveList.Items.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            }
            catch { continue; }
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Chip"),
                GroupName = "drives",
                Content = d.Name.TrimEnd('\\'),
                Tag = d.Name,
                IsChecked = d.Name.Equals(_drive, StringComparison.OrdinalIgnoreCase),
            };
            AutomationProperties_SetName(rb, $"Drive {d.Name}");
            rb.Checked += Drive_Checked;
            DriveList.Items.Add(rb);
        }
    }

    static void AutomationProperties_SetName(DependencyObject o, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(o, name);

    void Drive_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string root } || root.Equals(_drive, StringComparison.OrdinalIgnoreCase)) return;
        if (_busy) return;
        _drive = root;
        _log.Info($"Drive changed to {root}.");
        _scan = null;
        TopFolderList.ItemsSource = null;
        TopFoldersEmpty.Visibility = Visibility.Visible;
        ScanInfoText.Text = "No scan of this drive yet.";
        RefreshDrive();
        BuildTargets();
    }

    // ------------------------------------------------------------------ drive overview

    long FreeBytes()
    {
        try { return new DriveInfo(_drive).AvailableFreeSpace; } catch { return 0; }
    }

    void RefreshDrive()
    {
        try
        {
            var d = new DriveInfo(_drive);
            long total = d.TotalSize, free = d.AvailableFreeSpace, used = total - d.TotalFreeSpace;
            TotalText.Text = Units.Bytes(total);
            UsedText.Text = Units.Bytes(used);
            UsedBar.Value = total > 0 ? 100.0 * used / total : 0;
            FreeText.Text = Units.Bytes(free);
            FreePctText.Text = total > 0 ? $"{100.0 * free / total:0}% of the drive" : "";
            string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local disk" : d.VolumeLabel;
            DriveLabelText.Text = $"{d.DriveFormat} · {label} ({d.Name.TrimEnd('\\')})";
        }
        catch (Exception ex)
        {
            _log.Error($"Could not read drive {_drive}: {ex.Message}");
            TotalText.Text = UsedText.Text = FreeText.Text = "—";
        }
        UpdateTotals();
    }

    void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _log.Info("Refresh requested.");
        RefreshDrive();
        MeasureAll();
    }

    // ------------------------------------------------------------------ catalog

    TargetRow? RowOf(TargetKind kind) => _targets.FirstOrDefault(t => t.Target.Kind == kind);

    void BuildTargets()
    {
        _targets.Clear();
        foreach (var t in _catalog.Targets)
        {
            var row = new TargetRow(t);
            if (_args.Select.Count > 0 && !row.IsFileList) row.IsChecked = _args.Select.Contains(t.Id);
            row.Changed += UpdateTotals;
            _targets.Add(row);
        }
        _args.Select.Clear(); // only used once, right after an admin restart
        TargetList.ItemsSource = null;
        TargetList.ItemsSource = _targets;
        MeasureAll();
    }

    bool OnDrive(string path) =>
        string.Equals(System.IO.Path.GetPathRoot(path), _drive, StringComparison.OrdinalIgnoreCase);

    async void MeasureAll()
    {
        _measureCts?.Cancel();
        var cts = _measureCts = new CancellationTokenSource();
        var ct = cts.Token;
        string drive = _drive;
        _log.Info($"Measuring catalog targets on {drive}.");

        foreach (var row in _targets.ToList())
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                switch (row.Target.Kind)
                {
                    case TargetKind.RecycleBin:
                    {
                        var (bytes, items) = await Task.Run(() => Native.QueryRecycleBin(drive), ct);
                        row.Bytes = bytes;
                        row.Items = items;
                        row.Available = items > 0;
                        row.SizeText = items > 0 ? Units.Bytes(bytes) : "Empty";
                        row.Note = "";
                        break;
                    }
                    case TargetKind.Contents:
                    case TargetKind.WindowsTool:
                        await MeasureContentsRow(row, drive, ct);
                        break;
                    case TargetKind.Downloads:
                        await MeasureDownloadsRow(row, drive, ct);
                        break;
                    case TargetKind.LargeFiles:
                        BuildLargeRows();
                        break;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                row.Available = false;
                row.SizeText = "Error";
                row.Note = ex.Message;
                _log.Error($"Measuring '{row.Name}' failed: {ex.Message}");
            }
            UpdateTotals();
        }
        _log.Info("Measuring finished.");
    }

    async Task MeasureContentsRow(TargetRow row, string drive, CancellationToken ct)
    {
        row.SizeText = "Measuring…";
        var m = await Task.Run(() => TargetResolver.MeasureContents(row.Target, ct), ct);
        if (ct.IsCancellationRequested) return;

        m.Roots.RemoveAll(r => !OnDrive(r));
        m.Files.RemoveAll(f => !OnDrive(f.Path));
        m.Bytes = m.Files.Sum(f => f.Size);
        row.Measure = m;
        row.Bytes = m.Bytes;
        row.Items = m.Files.Count;
        foreach (var n in m.Notes) _log.Info($"{row.Name}: {n}");

        if (m.Roots.Count == 0)
        {
            row.Available = false;
            row.SizeText = "Not found";
            row.Note = "";
            return;
        }

        bool needsAdminToSee = m.Unreadable > 0 && !_isAdmin;
        row.SizeText = Units.Bytes(m.Bytes) + (m.Unreadable > 0 ? "+" : "");
        row.Note = needsAdminToSee && row.Admin
            ? $"{m.Unreadable} folders need admin rights to read, so the real size is probably larger."
            : m.Unreadable > 0 ? $"{m.Unreadable} folders could not be read and will be skipped." : "";

        if (row.Target.Kind == TargetKind.WindowsTool)
            row.Available = true;
        else if (m.Files.Count == 0 && !(needsAdminToSee && row.Admin))
        {
            row.Available = false;
            row.SizeText = "Already clean";
        }
        else row.Available = true;
    }

    async Task MeasureDownloadsRow(TargetRow row, string drive, CancellationToken ct)
    {
        string? dl = Native.DownloadsFolder();
        InstallersSub.Text = dl == null ? "Downloads folder not found." :
            $"{dl}  ·  {string.Join(", ", row.Target.Extensions)} over {row.Target.MinSizeMB} MB";
        row.Files.Clear();
        if (dl == null || !OnDrive(dl))
        {
            row.Available = false;
            row.SizeText = dl == null ? "Not found" : "On another drive";
            InstallerList.ItemsSource = null;
            InstallersEmpty.Text = "Your Downloads folder is not on this drive.";
            InstallersEmpty.Visibility = Visibility.Visible;
            return;
        }
        var m = await Task.Run(() => TargetResolver.MeasureDownloads(row.Target, dl, ct), ct);
        foreach (var f in m.Files)
        {
            var v = PathSafety.CheckOnDisk(f.Path);
            var fr = new FileRow(f.Path, f.Size, f.Modified, v.Allowed ? null : v.Reason);
            fr.Changed += () => { row.RefreshFileState(); UpdateTotals(); };
            row.Files.Add(fr);
        }
        row.Available = row.Files.Any(f => !f.IsLocked);
        InstallerList.ItemsSource = row.Files.ToList();
        InstallersEmpty.Text = "No installers or archives over the size limit. Nothing to review here.";
        InstallersEmpty.Visibility = row.Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        row.SizeText = row.Files.Count == 0 ? "None found" : Units.Bytes(m.Bytes);
        row.RefreshFileState();
    }

    void BuildLargeRows()
    {
        var row = RowOf(TargetKind.LargeFiles);
        if (row == null) return;
        row.Files.Clear();

        if (_scan == null || !OnDrive(_scan.Root.Path) || _scan.Root.Path.Length > 3)
        {
            row.Available = false;
            row.SizeText = "Scan first";
            row.ChooseText = "";
            LargeList.ItemsSource = null;
            LargeEmpty.Text = "Scan the drive first (Overview > Scan drive). Large files then show up here.";
            LargeEmpty.Visibility = Visibility.Visible;
            return;
        }

        foreach (var f in _scan.LargeFiles.Where(f => f.Size >= _threshold))
        {
            if (!File.Exists(f.Path)) continue;
            var v = PathSafety.CheckOnDisk(f.Path);
            var fr = new FileRow(f.Path, f.Size, f.Modified, v.Allowed ? null : v.Reason);
            fr.Changed += () => { row.RefreshFileState(); UpdateTotals(); };
            row.Files.Add(fr);
        }
        row.Available = row.Files.Any(f => !f.IsLocked);
        LargeList.ItemsSource = row.Files.ToList();
        LargeEmpty.Text = $"No files over {_threshold / Units.MB:N0} MB found.";
        LargeEmpty.Visibility = row.Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        row.SizeText = row.Files.Count == 0 ? "None found" : "";
        row.RefreshFileState();
        if (row.Files.Count > 0 && row.SelectedItems == 0)
            row.SizeText = $"{row.Files.Count(f => !f.IsLocked)} files";
    }

    void UpdateTotals()
    {
        if (TotalText == null) return;
        long free = FreeBytes();
        long sel = _targets.Sum(t => t.SelectedBytes);
        long items = _targets.Sum(t => t.SelectedItems);
        int count = _targets.Count(t => t.IsSelected);
        long after = free + sel;

        AfterText.Text = Units.Bytes(after);
        AfterNote.Text = after >= _goalBytes ? $"Goal of {Units.Bytes(_goalBytes)} reached" : $"{Units.Bytes(_goalBytes - after)} short of {Units.Bytes(_goalBytes)}";
        SelTotalText.Text = Units.Bytes(sel);
        SelCountText.Text = count == 0 ? "nothing selected" : $"{items:N0} items in {count} target{(count == 1 ? "" : "s")}";

        var adminSel = _targets.Where(t => t.IsSelected && t.Admin).ToList();
        if (adminSel.Count > 0)
        {
            AdminNoteBox.Visibility = Visibility.Visible;
            AdminNoteText.Text = _isAdmin
                ? "Running as administrator, so admin-only items will be cleaned too."
                : $"{adminSel.Count} selected item{(adminSel.Count == 1 ? " needs" : "s need")} admin rights. Byterake will explain and ask before Windows shows its permission prompt.";
        }
        else AdminNoteBox.Visibility = Visibility.Collapsed;

        if (!_isAdmin && adminSel.Count > 0)
        {
            AdminBanner.Visibility = Visibility.Visible;
            AdminBannerText.Text = $"{string.Join(", ", adminSel.Select(a => a.Name))} need{(adminSel.Count == 1 ? "s" : "")} admin rights to measure and clean fully. Restarting keeps your ticks.";
        }
        else AdminBanner.Visibility = Visibility.Collapsed;

        var winOld = _targets.FirstOrDefault(t => t.Target.Kind == TargetKind.WindowsTool && t.IsSelected);
        WinOldWarn.Visibility = winOld != null && winOld.Target.Warning != null ? Visibility.Visible : Visibility.Collapsed;
        if (winOld?.Target.Warning != null) WinOldWarnText.Text = winOld.Target.Warning;

        double goal = Math.Max(_goalBytes, 1);
        RingNow.Data = Arc(free / goal);
        RingProjected.Data = Arc(after / goal);
        GoalPctText.Text = $"{Math.Min(100, 100.0 * after / goal):0}%";
        LegendFree.Text = Units.Bytes(free);
        LegendSelected.Text = Units.Bytes(sel);
        LegendNeeded.Text = after >= _goalBytes ? "Goal reached" : Units.Bytes(_goalBytes - after);

        var fileTicks = _targets.Where(t => t.IsFileList).SelectMany(t => t.Files).Where(f => f.IsChecked).ToList();
        ReviewTotal.Text = fileTicks.Count == 0 ? "No files ticked." : $"Ticked: {fileTicks.Count} files, {Units.Bytes(fileTicks.Sum(f => f.Size))}";
    }

    static Geometry Arc(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 0.9999);
        if (fraction < 0.002) return Geometry.Empty;
        const double r = 82, cx = 90, cy = 90;
        double a = fraction * 2 * Math.PI;
        var fig = new PathFigure { StartPoint = new Point(cx, cy - r), IsClosed = false };
        fig.Segments.Add(new ArcSegment(new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a)), new Size(r, r), 0,
            fraction > 0.5, SweepDirection.Clockwise, true));
        var g = new PathGeometry(new[] { fig });
        g.Freeze();
        return g;
    }

    void SelectSafe_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in _targets.Where(t => !t.IsFileList && t.IsSafe && t.Available)) t.IsChecked = true;
    }

    void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in _targets)
        {
            if (t.IsFileList) foreach (var f in t.Files) f.IsChecked = false;
            else t.IsChecked = false;
        }
    }

    void Goal_Changed(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(GoalBox.Text, out double gb) && gb > 0 && gb < 100000)
        {
            _goalBytes = (long)(gb * Units.GB);
            GoalTitle.Text = $"Goal: {gb:0.#} GB free";
        }
        else GoalBox.Text = (_goalBytes / (double)Units.GB).ToString("0.#");
        UpdateTotals();
    }

    void Goal_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Goal_Changed(sender, e);
    }

    // ------------------------------------------------------------------ admin

    void RestartAdmin_Click(object sender, RoutedEventArgs e)
    {
        var choice = Dialog.Ask(this, "Restart as administrator?",
            "Byterake will close and open again with administrator rights, so it can measure and clean the admin-only items. " +
            "Windows will show its permission prompt next.\n\nYour ticks in the catalog are kept. File ticks on the Review files page will need to be redone.",
            ("cancel", "Cancel", Tone.Ghost), ("go", "Restart as administrator", Tone.Primary));
        if (choice == "go") RestartAsAdmin();
    }

    void RestartAsAdmin()
    {
        string ids = string.Join(",", _targets.Where(t => t.IsChecked && !t.IsFileList).Select(t => t.Id));
        string args = $"--drive {_drive[0]}" + (ids.Length > 0 ? $" --select {ids}" : "");
        _log.Info("Asking Windows to restart Byterake as administrator.");
        if (SystemTools.RestartAsAdmin(args))
        {
            _log.Info("Restarting as administrator.");
            Application.Current.Shutdown();
        }
        else
        {
            _log.Info("Administrator permission was not given.");
            Dialog.Info(this, "Still running normally", "Windows permission was not given, so nothing changed. Admin-only items stay as they are.");
        }
    }

    // ------------------------------------------------------------------ clean

    async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var selected = _targets.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0)
        {
            Dialog.Info(this, "Nothing selected", "Tick at least one item in the cleanup catalog, or tick files on the Review files page.");
            return;
        }

        var needAdmin = selected.Where(t => t.Admin && !_isAdmin).ToList();
        if (needAdmin.Count > 0)
        {
            var choice = Dialog.Ask(this, "Administrator rights needed",
                "These selected items need administrator rights:\n\n• " + string.Join("\n• ", needAdmin.Select(n => n.Name)) +
                "\n\nByterake can restart as administrator; Windows will then ask for your permission and your catalog ticks are kept. " +
                "Or you can clean everything else now and leave these for later.",
                ("cancel", "Cancel", Tone.Ghost), ("others", "Clean the others only", Tone.Ghost), ("restart", "Restart as administrator", Tone.Primary));
            if (choice == "restart") { RestartAsAdmin(); return; }
            if (choice != "others") return;
            selected = selected.Except(needAdmin).ToList();
            if (selected.Count == 0) return;
        }

        string drive = _drive;
        var jobs = new List<CleanJob>();
        var lines = new List<string>();
        TargetRow? bin = null, winOld = null;
        int files = 0;
        long bytes = 0;

        foreach (var r in selected)
        {
            switch (r.Target.Kind)
            {
                case TargetKind.RecycleBin:
                    bin = r;
                    lines.Add($"Recycle Bin on {drive.TrimEnd('\\')}: {r.Items:N0} items, {Units.Bytes(r.Bytes)} (emptied for good)");
                    break;
                case TargetKind.WindowsTool:
                    winOld = r;
                    lines.Add($"{r.Name}: about {Units.Bytes(r.Bytes)}, removed by Windows Disk Cleanup");
                    break;
                case TargetKind.Contents:
                {
                    var m = r.Measure;
                    if (m == null || m.Files.Count == 0) break;
                    jobs.Add(new CleanJob { Label = r.Name, Files = m.Files.ToList(), AllowedRoots = m.Roots.ToList(), RemoveEmptyFolders = true });
                    files += m.Files.Count;
                    bytes += m.Bytes;
                    lines.Add($"{r.Name}: {m.Files.Count:N0} files, {Units.Bytes(m.Bytes)}");
                    break;
                }
                case TargetKind.Downloads:
                case TargetKind.LargeFiles:
                {
                    var ticked = r.Files.Where(f => f.IsChecked && !f.IsLocked).ToList();
                    if (ticked.Count == 0) break;
                    jobs.Add(new CleanJob
                    {
                        Label = r.Name,
                        Files = ticked.Select(f => new CleanFile(f.Path, f.Size, f.Modified)).ToList(),
                        AllowedRoots = ticked.Select(f => f.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    });
                    files += ticked.Count;
                    bytes += ticked.Sum(f => f.Size);
                    lines.Add($"{r.Name}: {ticked.Count:N0} files you ticked, {Units.Bytes(ticked.Sum(f => f.Size))}");
                    break;
                }
            }
        }

        if (files == 0 && bin == null && winOld == null)
        {
            Dialog.Info(this, "Nothing to clean", "The selected items are already empty.");
            return;
        }

        var info = new ConfirmInfo
        {
            Files = files,
            FileBytes = bytes,
            BinItems = bin?.Items ?? 0,
            BinBytes = bin?.Bytes ?? 0,
            FreeBefore = FreeBytes(),
            Lines = lines,
            ExtraNote = winOld == null ? null :
                "Windows.old: when the clean finishes, Windows Disk Cleanup opens. Tick “Previous Windows installation(s)” there and press OK. " +
                (winOld.Target.Warning ?? ""),
        };
        var confirm = new ConfirmWindow(info) { Owner = this };
        confirm.ShowDialog();
        if (confirm.Result is not CleanMode mode)
        {
            _log.Info("Clean cancelled at the confirmation screen.");
            return;
        }
        _log.Info($"User confirmed: {info.TotalItems:N0} items, {Units.Bytes(info.TotalBytes)}, mode {mode}.");

        _busy = true;
        SetCleaning(true);
        long freeBefore = FreeBytes();
        var cts = _cleanCts = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        var progress = new Progress<CleanProgress>(p =>
        {
            CleanBar.Value = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
            CleanProgressText.Text = p.Total == 0 ? "Finishing…" : $"{p.Done:N0} of {p.Total:N0}{TimeLeft(clock.Elapsed, p.Done, p.Total)}  ·  {p.Current}";
        });

        CleanReport report;
        long binFreed = 0;
        try
        {
            report = await Task.Run(() =>
            {
                // Empty the bin first, so files recycled in this run stay restorable.
                if (bin != null) binFreed = Cleaner.EmptyRecycleBin(drive, _log);
                return Cleaner.Run(jobs, mode, _log, progress, cts.Token);
            });
        }
        catch (Exception ex)
        {
            _log.Error($"Clean stopped by an error: {ex.Message}");
            Dialog.Info(this, "Clean stopped", $"Something went wrong and the clean stopped:\n\n{ex.Message}\n\nDetails are in the Activity log.");
            return;
        }
        finally
        {
            _busy = false;
            SetCleaning(false);
        }

        if (winOld != null)
        {
            try
            {
                SystemTools.OpenDiskCleanup(drive);
                _log.Info("Opened Windows Disk Cleanup for Windows.old.");
            }
            catch (Exception ex) { _log.Error($"Could not open Disk Cleanup: {ex.Message}"); }
        }

        RefreshDrive();
        long freeAfter = FreeBytes();
        _log.Info($"Free space on {drive}: {Units.Bytes(freeBefore)} before, {Units.Bytes(freeAfter)} after.");
        ShowResult(report, mode, freeBefore, freeAfter, binFreed, winOld != null);
        MeasureAll();
    }

    void ShowResult(CleanReport r, CleanMode mode, long before, long after, long binFreed, bool winOld)
    {
        var sb = new System.Text.StringBuilder();
        if (r.FilesRemoved > 0)
            sb.AppendLine(mode == CleanMode.RecycleBin
                ? $"Moved {r.FilesRemoved:N0} files ({Units.Bytes(r.BytesRemoved)}) to the Recycle Bin."
                : $"Deleted {r.FilesRemoved:N0} files ({Units.Bytes(r.BytesRemoved)}) permanently.");
        if (binFreed > 0) sb.AppendLine($"Emptied the Recycle Bin: {Units.Bytes(binFreed)}.");
        if (r.Cancelled) sb.AppendLine("You stopped the clean before it finished.");
        if (r.Failed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{r.Failed.Count:N0} files were skipped because they are in use or locked:");
            foreach (var f in r.Failed.Take(6)) sb.AppendLine($"• {f.Path}  ({f.Reason})");
            if (r.Failed.Count > 6) sb.AppendLine($"• …and {r.Failed.Count - 6:N0} more (full list in the Activity log)");
        }
        if (r.Refused.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{r.Refused.Count:N0} files were refused by the safety check and left alone. See the Activity log.");
        }
        sb.AppendLine();
        sb.AppendLine($"Free space: {Units.Bytes(before)} → {Units.Bytes(after)}");
        if (mode == CleanMode.RecycleBin && r.FilesRemoved > 0)
            sb.AppendLine($"Recycled files still use space until the Recycle Bin is emptied.");
        if (winOld)
            sb.AppendLine("\nWindows Disk Cleanup is open. Tick “Previous Windows installation(s)” there to remove Windows.old.");

        var buttons = mode == CleanMode.RecycleBin && r.FilesRemoved > 0
            ? new[] { ("done", "Done", Tone.Ghost), ("log", "View log", Tone.Ghost), ("empty", "Empty Recycle Bin now…", Tone.Primary) }
            : new[] { ("log", "View log", Tone.Ghost), ("done", "Done", Tone.Primary) };
        var choice = Dialog.Ask(this, "Clean finished", sb.ToString().TrimEnd(), buttons);
        if (choice == "log") NavLog.IsChecked = true;
        if (choice == "empty") EmptyBinAfterClean();
    }

    async void EmptyBinAfterClean()
    {
        string drive = _drive;
        var (bytes, items) = Native.QueryRecycleBin(drive);
        if (items == 0) { Dialog.Info(this, "Recycle Bin is empty", "There is nothing in the Recycle Bin on this drive."); return; }
        var ok = Dialog.Ask(this, "Empty the Recycle Bin?",
            $"{items:N0} items ({Units.Bytes(bytes)}) in the Recycle Bin on {drive.TrimEnd('\\')} will be deleted for good. " +
            "This includes anything that was already in the bin, not only what Byterake just moved. It cannot be undone.",
            ("cancel", "Keep them", Tone.Ghost), ("empty", "Empty permanently", Tone.Danger));
        if (ok != "empty") { _log.Info("Empty Recycle Bin declined."); return; }
        long before = FreeBytes();
        await Task.Run(() => Cleaner.EmptyRecycleBin(drive, _log));
        RefreshDrive();
        Dialog.Info(this, "Recycle Bin emptied", $"Free space: {Units.Bytes(before)} → {Units.Bytes(FreeBytes())}");
        MeasureAll();
    }

    /// <summary>"  ·  about 2 min left", once there is enough progress to guess from.</summary>
    static string TimeLeft(TimeSpan elapsed, int done, int total)
    {
        if (done <= 0 || done >= total || elapsed.TotalSeconds < 3) return "";
        double secs = elapsed.TotalSeconds / done * (total - done);
        string text = secs < 10 ? "a few seconds" : secs < 60 ? $"{Math.Ceiling(secs / 10) * 10:0} sec" : $"{Math.Ceiling(secs / 60):0} min";
        return $"  ·  about {text} left";
    }

    void SetCleaning(bool on)
    {
        CleanButton.IsEnabled = !on;
        CancelCleanButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CleanProgressPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        TargetList.IsEnabled = !on;
        DriveList.IsEnabled = !on;
        if (on) { CleanBar.Value = 0; CleanProgressText.Text = "Starting…"; }
    }

    void CancelClean_Click(object sender, RoutedEventArgs e) => _cleanCts?.Cancel();

    // ------------------------------------------------------------------ scan

    async void ScanDrive_Click(object sender, RoutedEventArgs e) => await RunScan(_drive);

    async void ScanFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder to scan" };
        if (dlg.ShowDialog(this) == true) await RunScan(dlg.FolderName);
    }

    void CancelScan_Click(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    async Task RunScan(string root)
    {
        if (_scanCts != null) return;
        NavScan.IsChecked = true;
        var cts = _scanCts = new CancellationTokenSource();
        ScanBar.Visibility = Visibility.Visible;
        CancelScanBtn.Visibility = Visibility.Visible;
        ScanDriveBtn2.IsEnabled = false;
        ScanStatus.Text = $"Scanning {root}…";
        _log.Scan($"Scan started: {root}");

        var progress = new Progress<ScanProgress>(p =>
            ScanStatus.Text = $"{p.Files:N0} files  ·  {p.Folders:N0} folders  ·  {Units.Bytes(p.Bytes)}  ·  {p.CurrentFolder}");
        ScanResult r;
        try
        {
            r = await Task.Run(() => DiskScanner.Scan(root, Math.Min(_threshold, ScanFloor), 200, progress, cts.Token));
        }
        catch (Exception ex)
        {
            _log.Error($"Scan failed: {ex.Message}");
            ScanStatus.Text = $"Scan failed: {ex.Message}";
            return;
        }
        finally
        {
            _scanCts = null;
            ScanBar.Visibility = Visibility.Collapsed;
            CancelScanBtn.Visibility = Visibility.Collapsed;
            ScanDriveBtn2.IsEnabled = true;
        }

        string summary = $"{(r.Cancelled ? "Partial scan (cancelled)" : "Scanned")} {root}: {r.Files:N0} files, {r.Folders:N0} folders, " +
                         $"{Units.Bytes(r.Root.Size)} in {r.Elapsed:m\\:ss}. {r.Skipped.Count:N0} folders could not be read; {r.LinksSkipped:N0} links skipped.";
        _log.Scan(summary);
        foreach (var s in r.Skipped.Take(5000)) _log.Skipped(s.Path, $"not scanned: {s.Reason}");
        if (r.Skipped.Count > 5000) _log.Info($"…and {r.Skipped.Count - 5000:N0} more unreadable folders.");
        ScanStatus.Text = summary;

        FolderTree.ItemsSource = r.Root.Children.Take(200).Select(c => new FolderRow(c, r.Root.Size)).ToList();
        LargestFilesList.ItemsSource = r.LargestFiles.Take(100).Select(f =>
        {
            var v = PathSafety.Check(f.Path);
            return new FileRow(f.Path, f.Size, f.Modified, v.Allowed ? null : v.Reason);
        }).ToList();

        if (r.Root.Path.Length <= 3)
        {
            _scan = r;
            ScanInfoText.Text = $"Last scan {DateTime.Now:HH:mm}  ·  {r.Files:N0} files  ·  {r.Skipped.Count:N0} folders skipped (no access)" +
                                (r.Cancelled ? "  ·  partial" : "");
            TopFoldersTitle.Text = $"Largest folders on {r.Root.Path.TrimEnd('\\')}";
            ShowTopFolders(r);
            BuildLargeRows();
            UpdateTotals();
        }
    }

    /// <summary>Picks six folders that explain where the space went, opening up folders dominated by one child.</summary>
    void ShowTopFolders(ScanResult r)
    {
        var pool = r.Root.Children.Where(c => c.Size > 0).ToList();
        var picked = new List<DirNode>();
        while (picked.Count < 6 && pool.Count > 0)
        {
            var n = pool.MaxBy(x => x.Size)!;
            pool.Remove(n);
            int depth = n.Path.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
            bool open = n.Children.Count > 0 && depth < 4 && !PathSafety.ContentsProtected(n.Path)
                        && n.Children[0].Size >= n.Size / 4;
            if (open) pool.AddRange(n.Children.Where(c => c.Size > 0));
            else picked.Add(n);
        }
        long max = picked.Count == 0 ? 1 : Math.Max(1, picked.Max(p => p.Size));
        TopFolderList.ItemsSource = picked.Select(p =>
        {
            double pct = 100.0 * p.Size / max;
            return new TopFolder(p.Path, Units.Bytes(p.Size),
                new GridLength(Math.Max(pct, 0.001), GridUnitType.Star),
                new GridLength(Math.Max(100 - pct, 0.001), GridUnitType.Star),
                PathSafety.ContentsProtected(p.Path));
        }).ToList();
        TopFoldersEmpty.Visibility = picked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OpenTree_Click(object sender, RoutedEventArgs e) => NavScan.IsChecked = true;

    // ------------------------------------------------------------------ review files

    void ChooseFiles_Click(object sender, RoutedEventArgs e) => NavReview.IsChecked = true;

    void ApplyThreshold_Click(object sender, RoutedEventArgs e) => ApplyThreshold();

    void Threshold_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyThreshold();
    }

    void ApplyThreshold()
    {
        if (long.TryParse(ThresholdBox.Text, out long mb) && mb > 0)
        {
            if (mb * Units.MB < ScanFloor && _scan != null)
            {
                mb = ScanFloor / Units.MB;
                Dialog.Info(this, "Smallest size is 100 MB", "Scans list files from 100 MB up, so the limit was set to 100 MB.");
            }
            _threshold = mb * Units.MB;
            _log.Info($"Large-file size set to {mb} MB.");
        }
        ThresholdBox.Text = (_threshold / Units.MB).ToString();
        BuildLargeRows();
        UpdateTotals();
    }

    void OpenDownloads_Click(object sender, RoutedEventArgs e)
    {
        var dl = Native.DownloadsFolder();
        if (dl != null) SystemTools.OpenFolder(dl);
    }

    void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    void BackToOverview_Click(object sender, RoutedEventArgs e) => NavOverview.IsChecked = true;

    // ------------------------------------------------------------------ uninstall

    async void LoadApps()
    {
        _appsLoaded = true;
        AppsCount.Text = "Reading installed programs…";
        try
        {
            var list = await Task.Run(Uninstall.ListPrograms);
            _programs = list.Select(p => new ProgramRow(p)).ToList();
            _log.Info($"Listed {_programs.Count} installed programs.");
        }
        catch (Exception ex)
        {
            _log.Error($"Could not list programs: {ex.Message}");
            AppsCount.Text = "Could not read installed programs.";
            return;
        }
        ApplyApps();
    }

    void ApplyApps()
    {
        if (AppsList == null) return;
        string q = SearchBox.Text.Trim();
        IEnumerable<ProgramRow> rows = _programs;
        if (q.Length > 0)
            rows = rows.Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Details.Contains(q, StringComparison.OrdinalIgnoreCase));
        rows = _sort switch
        {
            "name" => rows.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
            "date" => rows.OrderByDescending(p => p.Program.InstallDate ?? DateTime.MinValue),
            _ => rows.OrderByDescending(p => p.Program.SizeBytes),
        };
        var list = rows.ToList();
        AppsList.ItemsSource = list;
        AppsCount.Text = $"{list.Count} programs";
    }

    void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyApps();
    }

    void Sort_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string s }) _sort = s;
        if (IsLoaded) ApplyApps();
    }

    void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProgramRow row }) return;
        var p = row.Program;
        var ok = Dialog.Ask(this, $"Uninstall {p.Name}?",
            $"Byterake will open {p.Name}'s own uninstaller. Follow its steps; Byterake does not delete any program files itself.\n\n" +
            $"Size: {row.SizeText}   Installed: {row.DateText}\nCommand: {p.UninstallCommand}",
            ("cancel", "Cancel", Tone.Ghost), ("go", "Open uninstaller", Tone.Primary));
        if (ok != "go") return;
        var error = Uninstall.Launch(p, _log);
        if (error != null) Dialog.Info(this, "Could not start the uninstaller", error);
    }

    // ------------------------------------------------------------------ navigation and log

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageOverview == null || sender is not RadioButton { Tag: string page }) return;
        foreach (var el in new FrameworkElement[] { PageOverview, PageScan, PageReview, PageApps, PageLog })
            el.Visibility = el.Name == page ? Visibility.Visible : Visibility.Collapsed;
        if (page == "PageApps" && !_appsLoaded) LoadApps();
        if (page == "PageLog") { FlushLog(); LogBox.ScrollToEnd(); }
    }

    void FlushLog()
    {
        if (_pendingLog.IsEmpty) return;
        var sb = new System.Text.StringBuilder();
        while (_pendingLog.TryDequeue(out var line)) sb.AppendLine(line);
        LogBox.AppendText(sb.ToString());
        if (LogBox.Text.Length > 600_000) LogBox.Text = LogBox.Text[^400_000..];
        if (PageLog.Visibility == Visibility.Visible) LogBox.ScrollToEnd();
    }

    void OpenLogFolder_Click(object sender, RoutedEventArgs e) => SystemTools.OpenFolder(_log.Folder);

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy)
        {
            var c = Dialog.Ask(this, "Cleaning is still running", "Stop cleaning and close Byterake? Files already handled stay handled; the rest are left alone.",
                ("stay", "Keep cleaning", Tone.Ghost), ("close", "Stop and close", Tone.Danger));
            if (c != "close") { e.Cancel = true; return; }
            _cleanCts?.Cancel();
        }
        _scanCts?.Cancel();
        _measureCts?.Cancel();
        _log.Info("Byterake closed.");
        _log.Dispose();
        base.OnClosing(e);
    }
}

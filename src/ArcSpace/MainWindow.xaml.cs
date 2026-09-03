using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ArcSpace.Controls;
using ArcSpace.Models;
using ArcSpace.Services;
using Microsoft.Win32;

namespace ArcSpace;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ScanItem> _liveFolderHotspots = [];
    private readonly List<ScanItem> _largestFiles = [];
    private readonly Stopwatch _scanStopwatch = new();
    private readonly DispatcherTimer _statusTimer;

    private CancellationTokenSource? _scanCancellation;
    private string _scanPath = string.Empty;
    private long _minimumFileSizeBytes;
    private long _latestFilesScanned;
    private long _latestDirectoriesScanned;
    private long _latestSkippedEntries;
    private long _latestBytesScanned;
    private int _scanGeneration;
    private bool _acceptScanProgress;
    private bool _isScanning;
    private bool _stopRequested;
    private ScanVisualState _scanVisualState;

    public MainWindow()
    {
        InitializeComponent();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatusDetails();

        PopulateDrives();
        SelectFileFilter(FilterAllButton, 0);
        SetScanVisualState(ScanVisualState.Ready);
        UpdateStatusDetails();
    }

    private void PopulateDrives()
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d => new DriveChoice(
                d.RootDirectory.FullName,
                $"{d.Name}  {GetDriveLabel(d)}  ({ScanItem.FormatBytes(d.AvailableFreeSpace)} free)"))
            .ToList();

        DriveCombo.ItemsSource = drives;

        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var selected = drives.FirstOrDefault(d => string.Equals(d.RootPath, systemRoot, StringComparison.OrdinalIgnoreCase))
                       ?? drives.FirstOrDefault();

        if (selected is not null)
        {
            DriveCombo.SelectedItem = selected;
            SetScanPath(selected.RootPath);
        }
        else
        {
            SetScanPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        await StartScanAsync();
    }

    private async Task StartScanAsync()
    {
        if (string.IsNullOrWhiteSpace(_scanPath) || !Directory.Exists(_scanPath))
        {
            MessageBox.Show(this, "Choose a valid drive or folder first.", "ArcSpace", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();
        var scanGeneration = ++_scanGeneration;

        _acceptScanProgress = true;
        _stopRequested = false;
        _latestFilesScanned = 0;
        _latestDirectoriesScanned = 0;
        _latestSkippedEntries = 0;
        _latestBytesScanned = 0;
        _largestFiles.Clear();
        _liveFolderHotspots.Clear();

        FolderTree.ItemsSource = _liveFolderHotspots;
        SpaceMap.ItemsSource = _liveFolderHotspots;
        SpaceMap.EmptyText = "Waiting for top-level folders to accumulate size…";
        LargestFilesGrid.ItemsSource = null;
        FilesScannedText.Text = "0";
        DirectoriesScannedText.Text = "0 folders";
        SkippedText.Text = "0";
        AnalyzedSpaceText.Text = "0 B";

        FolderSubtitleText.Text = "Live top-level hotspots · partial totals";
        FilesSubtitleText.Text = "Top 100 files found so far · partial results";
        FolderEmptyTitle.Text = "Scanning folders…";
        FolderEmptySubtitle.Text = "Major folders will appear as ArcSpace discovers their contents.";
        FolderEmptyState.Visibility = Visibility.Visible;
        FilesEmptyTitle.Text = "Scanning files…";
        FilesEmptySubtitle.Text = "Large files will appear here as they are discovered.";
        FilesEmptyState.Visibility = Visibility.Visible;

        SetScanningState(true);
        SetScanVisualState(ScanVisualState.Scanning);
        StatusText.Text = $"Scanning  ·  {_scanPath}";
        _scanStopwatch.Restart();
        _statusTimer.Start();
        UpdateStatusDetails();

        IDiskScanner scanner = new DiskScanner();
        var progress = new Progress<ScanProgress>(scanProgress =>
        {
            if (!_acceptScanProgress || scanGeneration != _scanGeneration)
            {
                return;
            }

            _latestFilesScanned = scanProgress.FilesScanned;
            _latestDirectoriesScanned = scanProgress.DirectoriesScanned;
            _latestSkippedEntries = scanProgress.SkippedEntries;
            _latestBytesScanned = scanProgress.BytesScanned;

            FilesScannedText.Text = scanProgress.FilesScanned.ToString("N0");
            DirectoriesScannedText.Text = $"{scanProgress.DirectoriesScanned:N0} folders";
            SkippedText.Text = scanProgress.SkippedEntries.ToString("N0");
            AnalyzedSpaceText.Text = ScanItem.FormatBytes(scanProgress.BytesScanned);

            if (scanProgress.Snapshot is not null)
            {
                ApplyLiveFolderSnapshot(scanProgress.Snapshot.FolderHotspots, scanProgress.BytesScanned);
                ApplyLiveLargestFileSnapshot(scanProgress.Snapshot.LargestFiles);
            }

            StatusText.Text = _stopRequested
                ? "Stopping scan  ·  partial results will be kept"
                : $"Scanning  ·  {scanProgress.CurrentPath}";
            UpdateStatusDetails();
        });

        try
        {
            var result = await scanner.ScanAsync(_scanPath, progress, _scanCancellation.Token);
            if (scanGeneration != _scanGeneration)
            {
                return;
            }

            _acceptScanProgress = false;
            ApplyScanResult(result);
        }
        catch (OperationCanceledException)
        {
            if (scanGeneration != _scanGeneration)
            {
                return;
            }

            _acceptScanProgress = false;
            SetScanVisualState(ScanVisualState.Cancelled);
            FolderSubtitleText.Text = "Partial live hotspots retained after cancellation";
            FilesSubtitleText.Text = "Largest files discovered before cancellation";
            FolderEmptyTitle.Text = "Scan cancelled";
            FolderEmptySubtitle.Text = "Any partial folders discovered before cancellation remain available.";
            FolderEmptyState.Visibility = _liveFolderHotspots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SpaceMap.EmptyText = "No folder areas were measured before cancellation.";
            ApplyLargestFileFilter();
            StatusText.Text = "Scan cancelled  ·  partial results retained";
        }
        catch (Exception ex)
        {
            if (scanGeneration != _scanGeneration)
            {
                return;
            }

            _acceptScanProgress = false;
            SetScanVisualState(ScanVisualState.Error);
            FolderSubtitleText.Text = "Partial live hotspots captured before the error";
            FilesSubtitleText.Text = "Largest files captured before the error";
            FolderEmptyTitle.Text = "Scan could not continue";
            FolderEmptySubtitle.Text = "Any partial results already discovered are still available.";
            FolderEmptyState.Visibility = _liveFolderHotspots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SpaceMap.EmptyText = "No folder areas were measured before the scan stopped.";
            ApplyLargestFileFilter();
            StatusText.Text = "Scan failed  ·  partial results retained";
            MessageBox.Show(this, ex.Message, "ArcSpace scan error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (scanGeneration == _scanGeneration)
            {
                _acceptScanProgress = false;
                _scanStopwatch.Stop();
                _statusTimer.Stop();
                SetScanningState(false);
                UpdateStatusDetails();
            }
        }
    }

    private void ApplyScanResult(ScanResult result)
    {
        _latestFilesScanned = result.Root.FileCount;
        _latestDirectoriesScanned = result.DirectoriesScanned;
        _latestSkippedEntries = result.SkippedEntries;
        _latestBytesScanned = result.Root.SizeBytes;

        FilesScannedText.Text = result.Root.FileCount.ToString("N0");
        DirectoriesScannedText.Text = $"{result.DirectoriesScanned:N0} folders";
        SkippedText.Text = result.SkippedEntries.ToString("N0");
        AnalyzedSpaceText.Text = result.Root.SizeDisplay;

        UpdateUsagePercentages(result.Root);
        SetScanVisualState(result.WasCancelled ? ScanVisualState.Cancelled : ScanVisualState.Complete);
        FolderSubtitleText.Text = result.WasCancelled
            ? "Partial hierarchy preserved at cancellation"
            : "Largest folders first, with file counts";
        FilesSubtitleText.Text = result.WasCancelled
            ? "Top files discovered before cancellation"
            : "Top 100 files discovered during this scan";

        FolderTree.ItemsSource = new[] { result.Root };
        SpaceMap.ItemsSource = result.Root.Children.Any(child => child.SizeBytes > 0)
            ? result.Root.Children
            : new[] { result.Root };
        SpaceMap.EmptyText = result.WasCancelled
            ? "No folder areas were measured before cancellation."
            : "No measurable folder areas were found.";
        FolderEmptyState.Visibility = Visibility.Collapsed;
        _liveFolderHotspots.Clear();

        _largestFiles.Clear();
        _largestFiles.AddRange(result.LargestFiles);
        ApplyLargestFileFilter();
        ExpandRootFolder(result.Root);

        StatusText.Text = result.WasCancelled
            ? $"Scan cancelled  ·  {result.Root.SizeDisplay} analyzed before stop"
            : $"Scan complete  ·  {result.Root.SizeDisplay} analyzed";
    }

    private void ApplyLiveFolderSnapshot(IReadOnlyList<FolderHotspot> hotspots, long rootSizeBytes)
    {
        var desiredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hotspot in hotspots)
        {
            desiredPaths.Add(hotspot.FullPath);
        }

        for (var index = _liveFolderHotspots.Count - 1; index >= 0; index--)
        {
            if (!desiredPaths.Contains(_liveFolderHotspots[index].FullPath))
            {
                _liveFolderHotspots.RemoveAt(index);
            }
        }

        for (var desiredIndex = 0; desiredIndex < hotspots.Count; desiredIndex++)
        {
            var hotspot = hotspots[desiredIndex];
            var existingIndex = IndexOfPath(_liveFolderHotspots, hotspot.FullPath);
            ScanItem item;

            if (existingIndex >= 0)
            {
                item = _liveFolderHotspots[existingIndex];
                item.SizeBytes = hotspot.SizeBytes;
                item.FileCount = hotspot.FileCount;
            }
            else
            {
                item = new ScanItem
                {
                    Name = hotspot.Name,
                    FullPath = hotspot.FullPath,
                    IsDirectory = true,
                    SizeBytes = hotspot.SizeBytes,
                    FileCount = hotspot.FileCount
                };
                _liveFolderHotspots.Insert(Math.Min(desiredIndex, _liveFolderHotspots.Count), item);
                existingIndex = _liveFolderHotspots.IndexOf(item);
            }

            if (existingIndex != desiredIndex)
            {
                _liveFolderHotspots.Move(existingIndex, desiredIndex);
            }
        }

        UpdateTopLevelUsagePercentages(_liveFolderHotspots, rootSizeBytes);
        FolderEmptyState.Visibility = _liveFolderHotspots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void UpdateUsagePercentages(ScanItem root)
    {
        root.UsagePercent = 100d;

        var pending = new Stack<ScanItem>();
        pending.Push(root);

        while (pending.TryPop(out var parent))
        {
            foreach (var child in parent.Children)
            {
                child.UsagePercent = child.PercentOf(parent.SizeBytes);
                pending.Push(child);
            }
        }
    }

    private static void UpdateTopLevelUsagePercentages(IReadOnlyList<ScanItem> items, long parentSize)
    {
        var effectiveParentSize = parentSize > 0 ? parentSize : items.Sum(item => item.SizeBytes);
        foreach (var item in items)
        {
            item.UsagePercent = item.PercentOf(effectiveParentSize);
        }
    }

    private static int IndexOfPath(IList<ScanItem> items, string path)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (string.Equals(items[index].FullPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private void ApplyLiveLargestFileSnapshot(IReadOnlyList<ScanItem> largestFiles)
    {
        _largestFiles.Clear();
        _largestFiles.AddRange(largestFiles);
        ApplyLargestFileFilter();
    }

    private void ExpandRootFolder(ScanItem root)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (FolderTree.ItemContainerGenerator.ContainerFromItem(root) is TreeViewItem container)
            {
                container.IsExpanded = true;
            }
        }));
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        RequestScanCancellation();
    }

    private void RequestScanCancellation()
    {
        if (!_isScanning || _stopRequested)
        {
            return;
        }

        _stopRequested = true;
        _scanCancellation?.Cancel();
        StopButton.Content = "Stopping…";
        StopButton.IsEnabled = false;
        ScanStateText.Text = "STOPPING · PARTIAL";
        StatusText.Text = "Stopping scan  ·  partial results will be kept";
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder to scan",
            InitialDirectory = Directory.Exists(_scanPath) ? _scanPath : null,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            DriveCombo.SelectedItem = null;
            SetScanPath(dialog.FolderName);
        }
    }

    private void DriveCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveCombo.SelectedItem is DriveChoice drive)
        {
            SetScanPath(drive.RootPath);
        }
    }

    private void SetScanPath(string path)
    {
        _scanPath = path;
        PathText.Text = path;
        UpdateDiskSummary(path);
    }

    private void UpdateDiskSummary(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new IOException("No drive root available.");
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                throw new IOException("Drive is not ready.");
            }

            var used = drive.TotalSize - drive.AvailableFreeSpace;
            var percent = drive.TotalSize <= 0 ? 0 : (double)used / drive.TotalSize * 100d;

            DiskSummaryText.Text = $"{drive.Name}  {ScanItem.FormatBytes(used)} of {ScanItem.FormatBytes(drive.TotalSize)} used";
            DiskPercentText.Text = $"{percent:0.#}%";
            DiskUsageBar.Value = Math.Clamp(percent, 0, 100);
            UsedSpaceText.Text = ScanItem.FormatBytes(used);
            FreeSpaceText.Text = ScanItem.FormatBytes(drive.AvailableFreeSpace);
        }
        catch
        {
            DiskSummaryText.Text = "Folder scan";
            DiskPercentText.Text = string.Empty;
            DiskUsageBar.Value = 0;
            UsedSpaceText.Text = "—";
            FreeSpaceText.Text = "—";
        }
    }

    private void SetScanningState(bool isScanning)
    {
        _isScanning = isScanning;
        ScanButton.IsEnabled = !isScanning;
        StopButton.IsEnabled = isScanning && !_stopRequested;
        StopButton.Content = isScanning && _stopRequested ? "Stopping…" : "Stop";
        DriveCombo.IsEnabled = !isScanning;
        ChooseFolderButton.IsEnabled = !isScanning;
        ScanActivityBar.Visibility = isScanning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetScanVisualState(ScanVisualState state)
    {
        _scanVisualState = state;

        switch (state)
        {
            case ScanVisualState.Scanning:
                ScanStateText.Text = "SCANNING · PARTIAL";
                ScanStateBadge.Background = ResourceBrush("AccentSoftBrush");
                ScanStateText.Foreground = ResourceBrush("AccentHoverBrush");
                StatusDot.Fill = ResourceBrush("AccentHoverBrush");
                SpaceMapStateText.Text = "LIVE · PARTIAL";
                SpaceMapStateBadge.Background = ResourceBrush("AccentSoftBrush");
                SpaceMapStateText.Foreground = ResourceBrush("AccentHoverBrush");
                break;
            case ScanVisualState.Complete:
                ScanStateText.Text = "COMPLETE";
                ScanStateBadge.Background = ResourceBrush("SuccessSoftBrush");
                ScanStateText.Foreground = ResourceBrush("SuccessBrush");
                StatusDot.Fill = ResourceBrush("SuccessBrush");
                SpaceMapStateText.Text = "COMPLETE";
                SpaceMapStateBadge.Background = ResourceBrush("SuccessSoftBrush");
                SpaceMapStateText.Foreground = ResourceBrush("SuccessBrush");
                break;
            case ScanVisualState.Cancelled:
                ScanStateText.Text = "CANCELLED · PARTIAL";
                ScanStateBadge.Background = ResourceBrush("SurfaceMutedBrush");
                ScanStateText.Foreground = ResourceBrush("TextSecondaryBrush");
                StatusDot.Fill = ResourceBrush("TextTertiaryBrush");
                SpaceMapStateText.Text = "PARTIAL";
                SpaceMapStateBadge.Background = ResourceBrush("SurfaceMutedBrush");
                SpaceMapStateText.Foreground = ResourceBrush("TextSecondaryBrush");
                break;
            case ScanVisualState.Error:
                ScanStateText.Text = "ERROR · PARTIAL";
                ScanStateBadge.Background = ResourceBrush("DangerSoftBrush");
                ScanStateText.Foreground = ResourceBrush("DangerBrush");
                StatusDot.Fill = ResourceBrush("DangerBrush");
                SpaceMapStateText.Text = "PARTIAL";
                SpaceMapStateBadge.Background = ResourceBrush("DangerSoftBrush");
                SpaceMapStateText.Foreground = ResourceBrush("DangerBrush");
                break;
            default:
                ScanStateText.Text = "READY";
                ScanStateBadge.Background = ResourceBrush("SuccessSoftBrush");
                ScanStateText.Foreground = ResourceBrush("SuccessBrush");
                StatusDot.Fill = ResourceBrush("SuccessBrush");
                SpaceMapStateText.Text = "WAITING";
                SpaceMapStateBadge.Background = ResourceBrush("SurfaceMutedBrush");
                SpaceMapStateText.Foreground = ResourceBrush("TextTertiaryBrush");
                break;
        }
    }

    private Brush ResourceBrush(string key) => (Brush)FindResource(key);

    private void UpdateStatusDetails()
    {
        var elapsed = _scanStopwatch.Elapsed;
        var time = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        var details = $"{_latestFilesScanned:N0} files  ·  {_latestDirectoriesScanned:N0} folders  ·  {ScanItem.FormatBytes(_latestBytesScanned)}  ·  {time}";
        if (_scanStopwatch.IsRunning && elapsed.TotalSeconds >= 0.5)
        {
            var filesPerSecond = _latestFilesScanned / elapsed.TotalSeconds;
            details += $"  ·  {filesPerSecond:N0} files/s";
        }

        StatusDetailsText.Text = details;
    }

    private void FilterAll_Click(object sender, RoutedEventArgs e) => SelectFileFilter(FilterAllButton, 0);

    private void Filter100_Click(object sender, RoutedEventArgs e) => SelectFileFilter(Filter100Button, 100L * 1024 * 1024);

    private void Filter500_Click(object sender, RoutedEventArgs e) => SelectFileFilter(Filter500Button, 500L * 1024 * 1024);

    private void Filter1Gb_Click(object sender, RoutedEventArgs e) => SelectFileFilter(Filter1GbButton, 1024L * 1024 * 1024);

    private void SelectFileFilter(Button activeButton, long minimumBytes)
    {
        _minimumFileSizeBytes = minimumBytes;

        foreach (var button in new[] { FilterAllButton, Filter100Button, Filter500Button, Filter1GbButton })
        {
            button.Style = (Style)FindResource(button == activeButton ? "ActiveFilterButtonStyle" : "FilterButtonStyle");
        }

        Filter1GbButton.Margin = new Thickness(0);
        ApplyLargestFileFilter();
    }

    private void ApplyLargestFileFilter()
    {
        var selectedPath = (LargestFilesGrid.SelectedItem as ScanItem)?.FullPath;
        var filtered = _largestFiles
            .Where(file => file.SizeBytes >= _minimumFileSizeBytes)
            .OrderByDescending(file => file.SizeBytes)
            .ToList();

        LargestFilesGrid.ItemsSource = filtered;

        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            LargestFilesGrid.SelectedItem = filtered.FirstOrDefault(file =>
                string.Equals(file.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
        }

        FilesEmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (filtered.Count > 0)
        {
            return;
        }

        if (_largestFiles.Count > 0)
        {
            FilesEmptyTitle.Text = "No files match this filter";
            FilesEmptySubtitle.Text = _scanVisualState == ScanVisualState.Scanning
                ? "Lower the threshold to see files already discovered."
                : "Try a lower size threshold.";
            return;
        }

        switch (_scanVisualState)
        {
            case ScanVisualState.Scanning:
                FilesEmptyTitle.Text = "Scanning files…";
                FilesEmptySubtitle.Text = "Large files will appear here as they are discovered.";
                break;
            case ScanVisualState.Cancelled:
                FilesEmptyTitle.Text = "No large files found before cancellation";
                FilesEmptySubtitle.Text = "The partial folder results are still available.";
                break;
            case ScanVisualState.Error:
                FilesEmptyTitle.Text = "No large files captured";
                FilesEmptySubtitle.Text = "The scan stopped before any file reached this list.";
                break;
            case ScanVisualState.Complete:
                FilesEmptyTitle.Text = "No large files found";
                FilesEmptySubtitle.Text = "This scan did not return any files for the list.";
                break;
            default:
                FilesEmptyTitle.Text = "Waiting for scan";
                FilesEmptySubtitle.Text = "Large files will appear here automatically.";
                break;
        }
    }

    private void OpenFolderItem_Click(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is ScanItem item)
        {
            OpenExplorer(item.FullPath, selectFile: false);
        }
    }

    private void CopyFolderPath_Click(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is ScanItem item)
        {
            CopyPath(item.FullPath);
        }
    }

    private void DeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!CanDeleteDuringCurrentState())
        {
            return;
        }

        if (FolderTree.SelectedItem is not ScanItem item || !item.IsDirectory)
        {
            return;
        }

        var root = Path.GetPathRoot(item.FullPath)?.TrimEnd(Path.DirectorySeparatorChar);
        var selected = item.FullPath.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(root, selected, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "ArcSpace will not delete a drive root.", "Delete blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var choice = MessageBox.Show(
            this,
            $"Permanently delete this folder and everything inside it?\n\n{item.FullPath}\n\nThis does not use the Recycle Bin.",
            "Delete folder",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Directory.Delete(item.FullPath, recursive: true);
            StatusText.Text = $"Deleted {item.FullPath}. Rescan to refresh totals.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowFileInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (LargestFilesGrid.SelectedItem is ScanItem item)
        {
            OpenExplorer(item.FullPath, selectFile: true);
        }
    }

    private void CopyFilePath_Click(object sender, RoutedEventArgs e)
    {
        if (LargestFilesGrid.SelectedItem is ScanItem item)
        {
            CopyPath(item.FullPath);
        }
    }

    private void DeleteFile_Click(object sender, RoutedEventArgs e)
    {
        if (!CanDeleteDuringCurrentState())
        {
            return;
        }

        if (LargestFilesGrid.SelectedItem is not ScanItem item || item.IsDirectory)
        {
            return;
        }

        var choice = MessageBox.Show(
            this,
            $"Permanently delete this file?\n\n{item.FullPath}\n\nSize: {item.SizeDisplay}\n\nThis does not use the Recycle Bin.",
            "Delete file",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            File.Delete(item.FullPath);
            _largestFiles.RemoveAll(file =>
                string.Equals(file.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase));
            ApplyLargestFileFilter();
            StatusText.Text = $"Deleted {item.FullPath}. Rescan to refresh folder totals.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool CanDeleteDuringCurrentState()
    {
        if (!_isScanning)
        {
            return true;
        }

        MessageBox.Show(
            this,
            "Stop the active scan before deleting files or folders. Live results remain available while ArcSpace stops.",
            "Stop scan before deleting",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return false;
    }

    private void SpaceMap_ItemInvoked(object? sender, TreemapItemEventArgs e)
    {
        OpenExplorer(e.Item.FullPath, selectFile: false);
    }

    private void LargestFilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LargestFilesGrid.SelectedItem is ScanItem item)
        {
            OpenExplorer(item.FullPath, selectFile: true);
        }
    }

    private void FolderTree_KeyDown(object sender, KeyEventArgs e)
    {
        if (FolderTree.SelectedItem is not ScanItem item)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            OpenExplorer(item.FullPath, selectFile: false);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            CopyPath(item.FullPath);
            e.Handled = true;
        }
    }

    private void LargestFilesGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (LargestFilesGrid.SelectedItem is not ScanItem item)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            OpenExplorer(item.FullPath, selectFile: true);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            CopyPath(item.FullPath);
            e.Handled = true;
        }
    }

    private void FolderTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(FolderTree, source) is TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void LargestFilesGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(LargestFilesGrid, source) is DataGridRow row)
        {
            row.IsSelected = true;
            row.Focus();
        }
    }

    private void CopyPath(string path)
    {
        try
        {
            Clipboard.SetText(path);
            StatusText.Text = $"Copied: {path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not copy path: {ex.Message}";
        }
    }

    private void OpenExplorer(string path, bool selectFile)
    {
        try
        {
            var arguments = selectFile ? $"/select,\"{path}\"" : $"\"{path}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open Explorer: {ex.Message}";
        }
    }

    private static string GetDriveLabel(DriveInfo drive)
    {
        try
        {
            return string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
        }
        catch
        {
            return "Drive";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _acceptScanProgress = false;
        _scanGeneration++;
        _statusTimer.Stop();
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        base.OnClosed(e);
    }

    private enum ScanVisualState
    {
        Ready,
        Scanning,
        Complete,
        Cancelled,
        Error
    }

    private sealed record DriveChoice(string RootPath, string Display);
}

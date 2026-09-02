using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ArcSpace.Models;
using ArcSpace.Services;
using Microsoft.Win32;

namespace ArcSpace;

public partial class MainWindow : Window
{
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

    public MainWindow()
    {
        InitializeComponent();
        SetVersionLabel();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatusDetails();

        PopulateDrives();
        SelectFileFilter(FilterAllButton, 0);
        SetScanVisualState(ScanVisualState.Ready);
        UpdateStatusDetails();
    }

    private void SetVersionLabel()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        VersionText.Text = version is null
            ? "ArcSpace"
            : $"ArcSpace v{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
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

        _latestFilesScanned = 0;
        _latestDirectoriesScanned = 0;
        _latestSkippedEntries = 0;
        _latestBytesScanned = 0;
        _largestFiles.Clear();
        FolderTree.ItemsSource = null;
        LargestFilesGrid.ItemsSource = null;
        FilesScannedText.Text = "0";
        DirectoriesScannedText.Text = "0 folders";
        SkippedText.Text = "0";
        FolderEmptyState.Visibility = Visibility.Collapsed;
        FilesEmptyState.Visibility = Visibility.Visible;
        FilesEmptyTitle.Text = "Scanning drive…";
        FilesEmptySubtitle.Text = "Largest files will appear here live as they are discovered.";

        SetScanningState(true);
        SetScanVisualState(ScanVisualState.Scanning);
        StatusText.Text = $"Scanning {_scanPath}";
        _scanStopwatch.Restart();
        _statusTimer.Start();
        UpdateStatusDetails();

        var scanner = new DiskScanner();
        var progress = new Progress<ScanProgress>(p =>
        {
            _latestFilesScanned = p.FilesScanned;
            _latestDirectoriesScanned = p.DirectoriesScanned;
            _latestSkippedEntries = p.SkippedEntries;
            _latestBytesScanned = p.BytesScanned;

            FilesScannedText.Text = p.FilesScanned.ToString("N0");
            DirectoriesScannedText.Text = $"{p.DirectoriesScanned:N0} folders";
            SkippedText.Text = p.SkippedEntries.ToString("N0");
            StatusText.Text = $"Scanning  ·  {p.CurrentPath}";

            if (p.LargestFiles.Count > 0)
            {
                _largestFiles.Clear();
                _largestFiles.AddRange(p.LargestFiles);
                ApplyLargestFileFilter();
            }

            UpdateStatusDetails();
        });

        try
        {
            var result = await scanner.ScanAsync(_scanPath, progress, _scanCancellation.Token);
            FolderTree.ItemsSource = new[] { result.Root };
            FolderEmptyState.Visibility = Visibility.Collapsed;

            _largestFiles.Clear();
            _largestFiles.AddRange(result.LargestFiles);
            ApplyLargestFileFilter();

            _latestFilesScanned = result.Root.FileCount;
            _latestBytesScanned = result.Root.SizeBytes;
            _latestSkippedEntries = result.SkippedEntries;
            FilesScannedText.Text = result.Root.FileCount.ToString("N0");
            SkippedText.Text = result.SkippedEntries.ToString("N0");

            StatusText.Text = $"Scan complete  ·  {result.Root.SizeDisplay} analyzed";
            SetScanVisualState(ScanVisualState.Complete);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Scan cancelled";
            FolderEmptyState.Visibility = FolderTree.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            FilesEmptyTitle.Text = "Scan cancelled";
            FilesEmptySubtitle.Text = _largestFiles.Count > 0
                ? "Partial largest-file results are still available."
                : "Start another scan when you are ready.";
            FilesEmptyState.Visibility = _largestFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetScanVisualState(ScanVisualState.Cancelled);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Scan failed";
            FolderEmptyState.Visibility = Visibility.Visible;
            FilesEmptyTitle.Text = "Scan could not complete";
            FilesEmptySubtitle.Text = _largestFiles.Count > 0
                ? "Partial largest-file results are still available."
                : "Review the error and try again.";
            FilesEmptyState.Visibility = _largestFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetScanVisualState(ScanVisualState.Error);
            MessageBox.Show(this, ex.Message, "ArcSpace scan error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _scanStopwatch.Stop();
            _statusTimer.Stop();
            SetScanningState(false);
            UpdateStatusDetails();
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _scanCancellation?.Cancel();
        StatusText.Text = "Stopping scan…";
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

    private async void ScanFolderItem_Click(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is not ScanItem item || !item.IsDirectory)
        {
            return;
        }

        DriveCombo.SelectedItem = null;
        SetScanPath(item.FullPath);
        await StartScanAsync();
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
        ScanButton.IsEnabled = !isScanning;
        StopButton.IsEnabled = isScanning;
        DriveCombo.IsEnabled = !isScanning;
        ChooseFolderButton.IsEnabled = !isScanning;
        ScanActivityBar.Visibility = isScanning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetScanVisualState(ScanVisualState state)
    {
        switch (state)
        {
            case ScanVisualState.Scanning:
                ScanStateText.Text = "SCANNING";
                ScanStateBadge.Background = ResourceBrush("AccentSoftBrush");
                ScanStateText.Foreground = ResourceBrush("AccentBrush");
                StatusDot.Fill = ResourceBrush("AccentBrush");
                break;
            case ScanVisualState.Complete:
                ScanStateText.Text = "COMPLETE";
                ScanStateBadge.Background = ResourceBrush("SuccessSoftBrush");
                ScanStateText.Foreground = ResourceBrush("SuccessBrush");
                StatusDot.Fill = ResourceBrush("SuccessBrush");
                break;
            case ScanVisualState.Cancelled:
                ScanStateText.Text = "CANCELLED";
                ScanStateBadge.Background = ResourceBrush("SurfaceMutedBrush");
                ScanStateText.Foreground = ResourceBrush("TextSecondaryBrush");
                StatusDot.Fill = ResourceBrush("TextTertiaryBrush");
                break;
            case ScanVisualState.Error:
                ScanStateText.Text = "ERROR";
                ScanStateBadge.Background = ResourceBrush("DangerSoftBrush");
                ScanStateText.Foreground = ResourceBrush("DangerBrush");
                StatusDot.Fill = ResourceBrush("DangerBrush");
                break;
            default:
                ScanStateText.Text = "READY";
                ScanStateBadge.Background = ResourceBrush("SuccessSoftBrush");
                ScanStateText.Foreground = ResourceBrush("SuccessBrush");
                StatusDot.Fill = ResourceBrush("SuccessBrush");
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

        var analyzed = ScanItem.FormatBytes(_latestBytesScanned);
        if (_scanStopwatch.IsRunning && elapsed.TotalSeconds > 0.25)
        {
            var filesPerSecond = _latestFilesScanned / elapsed.TotalSeconds;
            StatusDetailsText.Text = $"{_latestFilesScanned:N0} files  ·  {analyzed}  ·  {filesPerSecond:N0} files/s  ·  {time}";
            return;
        }

        StatusDetailsText.Text = $"{_latestFilesScanned:N0} files  ·  {_latestDirectoriesScanned:N0} folders  ·  {analyzed}  ·  {time}";
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
        var filtered = _largestFiles
            .Where(f => f.SizeBytes >= _minimumFileSizeBytes)
            .OrderByDescending(f => f.SizeBytes)
            .ToList();

        LargestFilesGrid.ItemsSource = filtered;

        if (_scanCancellation is not null && _scanCancellation.IsCancellationRequested)
        {
            return;
        }

        FilesEmptyState.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (filtered.Count == 0 && _largestFiles.Count > 0)
        {
            FilesEmptyTitle.Text = "No files match this filter";
            FilesEmptySubtitle.Text = _scanStopwatch.IsRunning
                ? "Still scanning. Try a lower threshold or keep watching."
                : "Try a lower size threshold.";
        }
        else if (filtered.Count == 0 && _scanStopwatch.IsRunning)
        {
            FilesEmptyTitle.Text = "Scanning drive…";
            FilesEmptySubtitle.Text = "Largest files will appear here live as they are discovered.";
        }
        else if (filtered.Count == 0)
        {
            FilesEmptyTitle.Text = "No large files found";
            FilesEmptySubtitle.Text = "This scan did not return any files for the list.";
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
            _largestFiles.Remove(item);
            ApplyLargestFileFilter();
            StatusText.Text = $"Deleted {item.FullPath}. Rescan to refresh folder totals.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LargestFilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LargestFilesGrid.SelectedItem is ScanItem item)
        {
            OpenExplorer(item.FullPath, selectFile: true);
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

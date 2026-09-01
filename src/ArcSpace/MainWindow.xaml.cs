using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ArcSpace.Models;
using ArcSpace.Services;
using Microsoft.Win32;

namespace ArcSpace;

public partial class MainWindow : Window
{
    private readonly List<ScanItem> _largestFiles = [];
    private CancellationTokenSource? _scanCancellation;
    private string _scanPath = string.Empty;

    public MainWindow()
    {
        InitializeComponent();
        PopulateDrives();
        PopulateLargestFileFilters();
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

    private void PopulateLargestFileFilters()
    {
        LargestFilterCombo.ItemsSource = new List<FileSizeFilter>
        {
            new("All files", 0),
            new("> 100 MB", 100L * 1024 * 1024),
            new("> 500 MB", 500L * 1024 * 1024),
            new("> 1 GB", 1024L * 1024 * 1024)
        };
        LargestFilterCombo.SelectedIndex = 0;
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
        SetScanningState(true);
        FolderTree.ItemsSource = null;
        _largestFiles.Clear();
        LargestFilesGrid.ItemsSource = null;

        var scanner = new DiskScanner();
        var progress = new Progress<ScanProgress>(p =>
        {
            StatusText.Text = $"Scanning {p.FilesScanned:N0} files, {p.DirectoriesScanned:N0} folders  •  {ScanItem.FormatBytes(p.BytesScanned)}  •  {p.CurrentPath}";
        });

        try
        {
            var result = await scanner.ScanAsync(_scanPath, progress, _scanCancellation.Token);
            FolderTree.ItemsSource = new[] { result.Root };
            _largestFiles.AddRange(result.LargestFiles);
            ApplyLargestFileFilter();

            StatusText.Text = $"Complete  •  {result.Root.FileCount:N0} files  •  {result.Root.SizeDisplay}  •  {result.SkippedEntries:N0} inaccessible/reparse entries skipped";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Scan failed.";
            MessageBox.Show(this, ex.Message, "ArcSpace scan error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetScanningState(false);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _scanCancellation?.Cancel();
        StatusText.Text = "Stopping scan...";
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

            DiskSummaryText.Text = $"{drive.Name}  {ScanItem.FormatBytes(used)} used of {ScanItem.FormatBytes(drive.TotalSize)}  •  {ScanItem.FormatBytes(drive.AvailableFreeSpace)} free";
            DiskPercentText.Text = $"{percent:0.#}% used";
            DiskUsageBar.Value = Math.Clamp(percent, 0, 100);
        }
        catch
        {
            DiskSummaryText.Text = "Folder scan";
            DiskPercentText.Text = string.Empty;
            DiskUsageBar.Value = 0;
        }
    }

    private void SetScanningState(bool isScanning)
    {
        ScanButton.IsEnabled = !isScanning;
        StopButton.IsEnabled = isScanning;
        DriveCombo.IsEnabled = !isScanning;
    }

    private void LargestFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyLargestFileFilter();
    }

    private void ApplyLargestFileFilter()
    {
        var minimum = (LargestFilterCombo.SelectedItem as FileSizeFilter)?.MinimumBytes ?? 0;
        LargestFilesGrid.ItemsSource = _largestFiles
            .Where(f => f.SizeBytes >= minimum)
            .OrderByDescending(f => f.SizeBytes)
            .ToList();
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
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        base.OnClosed(e);
    }

    private sealed record DriveChoice(string RootPath, string Display);
    private sealed record FileSizeFilter(string Display, long MinimumBytes);
}

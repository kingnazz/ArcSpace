using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ArcSpace.Controls;
using ArcSpace.Models;
using Microsoft.Win32;

namespace ArcSpace;

public partial class MainWindow
{
    private const int MaximumPathHistory = 20;

    private readonly List<string> _scanPathHistory = [];
    private bool _suppressPathHistory;
    private string _largestFileSearch = string.Empty;

    private void InstallRefinementControls()
    {
        UpdateNavigationButtons();
        UpdateFileResultCount(0);
        ClearSelectionDetails();
    }

    private void RecordScanPathChange(string currentPath, string nextPath)
    {
        if (_suppressPathHistory ||
            string.IsNullOrWhiteSpace(currentPath) ||
            string.Equals(currentPath, nextPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_scanPathHistory.Count == 0 ||
            !string.Equals(_scanPathHistory[^1], currentPath, StringComparison.OrdinalIgnoreCase))
        {
            if (_scanPathHistory.Count >= MaximumPathHistory)
            {
                _scanPathHistory.RemoveAt(0);
            }

            _scanPathHistory.Add(currentPath);
        }
    }

    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = !_isScanning && _scanPathHistory.Count > 0;
        RefreshDrivesButton.IsEnabled = !_isScanning;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => NavigateBack();

    private void NavigateBack()
    {
        if (_isScanning || _scanPathHistory.Count == 0)
        {
            return;
        }

        var target = _scanPathHistory[^1];
        _scanPathHistory.RemoveAt(_scanPathHistory.Count - 1);

        _suppressPathHistory = true;
        try
        {
            NavigateToScanPath(target);
        }
        finally
        {
            _suppressPathHistory = false;
            UpdateNavigationButtons();
        }

        StatusText.Text = $"Scan target restored  ·  {target}";
    }

    private void RefreshDrivesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning)
        {
            return;
        }

        var currentPath = _scanPath;
        _suppressPathHistory = true;
        try
        {
            PopulateDrives();
            if (!string.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath))
            {
                NavigateToScanPath(currentPath);
            }
        }
        finally
        {
            _suppressPathHistory = false;
            UpdateNavigationButtons();
        }

        StatusText.Text = "Drive list refreshed";
    }

    private void NavigateToScanPath(string path)
    {
        var matchingDrive = DriveCombo.Items
            .OfType<DriveChoice>()
            .FirstOrDefault(choice => string.Equals(choice.RootPath, path, StringComparison.OrdinalIgnoreCase));

        if (matchingDrive is not null)
        {
            if (ReferenceEquals(DriveCombo.SelectedItem, matchingDrive))
            {
                SetScanPath(path);
            }
            else
            {
                DriveCombo.SelectedItem = matchingDrive;
            }

            return;
        }

        DriveCombo.SelectedItem = null;
        SetScanPath(path);
    }

    private void ScanFolderItem_Click(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is ScanItem { IsDirectory: true } item)
        {
            _ = ScanFolderAsync(item.FullPath);
        }
    }

    private void ScanContainingFolder_Click(object sender, RoutedEventArgs e)
    {
        if (LargestFilesGrid.SelectedItem is ScanItem item)
        {
            _ = ScanFolderAsync(Path.GetDirectoryName(item.FullPath));
        }
    }

    private void ScanSpaceMapFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SpaceMap.SelectedItem is not null)
        {
            _ = ScanFolderAsync(SpaceMap.SelectedItem.FullPath);
        }
    }

    private async Task ScanFolderAsync(string? path)
    {
        if (_isScanning || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        NavigateToScanPath(path);
        await StartScanAsync();
    }

    private async Task ScanSelectedLocationAsync()
    {
        var path = GetSelectedFolderForScan();
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText.Text = "Select a folder, space-map tile, or file before using Ctrl+Enter";
            return;
        }

        await ScanFolderAsync(path);
    }

    private string? GetSelectedFolderForScan()
    {
        if (LargestFilesGrid.IsKeyboardFocusWithin && LargestFilesGrid.SelectedItem is ScanItem focusedFile)
        {
            return Path.GetDirectoryName(focusedFile.FullPath);
        }

        if (SpaceMap.IsKeyboardFocusWithin && SpaceMap.SelectedItem is not null)
        {
            return SpaceMap.SelectedItem.FullPath;
        }

        if (FolderTree.IsKeyboardFocusWithin && FolderTree.SelectedItem is ScanItem focusedFolder)
        {
            return focusedFolder.FullPath;
        }

        if (FolderTree.SelectedItem is ScanItem folder)
        {
            return folder.FullPath;
        }

        if (SpaceMap.SelectedItem is not null)
        {
            return SpaceMap.SelectedItem.FullPath;
        }

        return LargestFilesGrid.SelectedItem is ScanItem file
            ? Path.GetDirectoryName(file.FullPath)
            : null;
    }

    private void CollapseFoldersButton_Click(object sender, RoutedEventArgs e)
    {
        var pending = new Stack<TreeViewItem>();
        foreach (var item in FolderTree.Items)
        {
            if (FolderTree.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem container)
            {
                pending.Push(container);
            }
        }

        while (pending.TryPop(out var container))
        {
            foreach (var child in container.Items)
            {
                if (container.ItemContainerGenerator.ContainerFromItem(child) is TreeViewItem childContainer)
                {
                    pending.Push(childContainer);
                }
            }

            container.IsExpanded = false;
        }

        StatusText.Text = "Folder hierarchy collapsed";
    }
    private void FileSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _largestFileSearch = FileSearchBox.Text.Trim();
        ApplyLargestFileFilter();
    }

    private bool MatchesLargestFileSearch(ScanItem file)
    {
        if (string.IsNullOrWhiteSpace(_largestFileSearch))
        {
            return true;
        }

        return file.Name.Contains(_largestFileSearch, StringComparison.OrdinalIgnoreCase) ||
               file.FullPath.Contains(_largestFileSearch, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateFileResultCount(int shownCount)
    {
        FileResultCountText.Text = shownCount == _largestFiles.Count
            ? $"{shownCount:N0} files"
            : $"{shownCount:N0} of {_largestFiles.Count:N0}";
    }

    private void FocusLargestFileSearch()
    {
        FileSearchBox.Focus();
        FileSearchBox.SelectAll();
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not ScanItem item)
        {
            return;
        }

        SpaceMap.SelectItem(item);
        ShowSelectionDetails(item, "Folder");
    }

    private void LargestFilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LargestFilesGrid.SelectedItem is ScanItem item)
        {
            ShowSelectionDetails(item, "File");
        }
    }

    private void SpaceMap_ItemSelected(object? sender, TreemapItemEventArgs e)
    {
        ShowSelectionDetails(e.Item, "Space map");
        SelectFolderTreeItemByPath(e.Item.FullPath);
    }

    private void ShowSelectionDetails(ScanItem item, string source)
    {
        SelectionText.Text = item.IsDirectory
            ? $"{source}  ·  {item.SizeDisplay}  ·  {item.FileCountDisplay} files  ·  {item.UsageDisplay} of parent  ·  {item.FullPath}"
            : $"{source}  ·  {item.SizeDisplay} ({item.SizeBytes:N0} bytes)  ·  {item.FullPath}";
        SelectionText.ToolTip = item.FullPath;
    }

    private void ClearSelectionDetails()
    {
        SelectionText.Text = "No item selected  ·  select a folder, file, or space-map tile for details";
        SelectionText.ToolTip = null;
    }

    private void SelectFolderTreeItemByPath(string path)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            foreach (var topLevelItem in FolderTree.Items.OfType<ScanItem>())
            {
                if (string.Equals(topLevelItem.FullPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    SelectTreeContainer(FolderTree.ItemContainerGenerator.ContainerFromItem(topLevelItem) as TreeViewItem);
                    return;
                }

                var child = topLevelItem.Children.FirstOrDefault(candidate =>
                    string.Equals(candidate.FullPath, path, StringComparison.OrdinalIgnoreCase));
                if (child is null ||
                    FolderTree.ItemContainerGenerator.ContainerFromItem(topLevelItem) is not TreeViewItem rootContainer)
                {
                    continue;
                }

                rootContainer.IsExpanded = true;
                rootContainer.UpdateLayout();
                SelectTreeContainer(rootContainer.ItemContainerGenerator.ContainerFromItem(child) as TreeViewItem);
                return;
            }
        }));
    }

    private static void SelectTreeContainer(TreeViewItem? container)
    {
        if (container is null)
        {
            return;
        }

        container.IsSelected = true;
        container.Focus();
        container.BringIntoView();
    }

    private void OpenSpaceMapFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SpaceMap.SelectedItem is not null)
        {
            OpenExplorer(SpaceMap.SelectedItem.FullPath, selectFile: false);
        }
    }

    private void CopySpaceMapPath_Click(object sender, RoutedEventArgs e)
    {
        if (SpaceMap.SelectedItem is not null)
        {
            CopyPath(SpaceMap.SelectedItem.FullPath);
        }
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e) => ExportCurrentResults();

    private void ExportCurrentResults()
    {
        var roots = FolderTree.ItemsSource?.OfType<ScanItem>().ToList() ?? [];
        var files = _largestFiles.OrderByDescending(file => file.SizeBytes).ToList();

        if (roots.Count == 0 && files.Count == 0 && _latestFilesScanned == 0)
        {
            MessageBox.Show(this, "Run a scan before exporting results.", "ArcSpace export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export ArcSpace results",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            AddExtension = true,
            FileName = BuildExportFileName()
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            using var writer = new StreamWriter(
                dialog.FileName,
                false,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var folderCount = WriteExportCsv(writer, roots, files);
            StatusText.Text = $"Exported {folderCount:N0} folders and {files.Count:N0} Top 100 entries  ·  {dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private int WriteExportCsv(
        TextWriter writer,
        IReadOnlyList<ScanItem> roots,
        IReadOnlyList<ScanItem> files)
    {
        AppendCsvRow(
            writer,
            "RecordType",
            "ResultState",
            "ScanTarget",
            "Name",
            "FullPath",
            "SizeBytes",
            "SizeDisplay",
            "FileCount",
            "FolderCount",
            "PercentOfParent",
            "Extension");

        var resultState = _scanVisualState.ToString();
        AppendCsvRow(
            writer,
            "Summary",
            resultState,
            _scanPath,
            "Scan totals",
            _scanPath,
            _latestBytesScanned,
            ScanItem.FormatBytes(_latestBytesScanned),
            _latestFilesScanned,
            _latestDirectoriesScanned,
            string.Empty,
            string.Empty);

        var folderCount = 0;
        foreach (var folder in FlattenFolders(roots))
        {
            folderCount++;
            AppendCsvRow(
                writer,
                "Folder",
                resultState,
                _scanPath,
                folder.Name,
                folder.FullPath,
                folder.SizeBytes,
                folder.SizeDisplay,
                folder.FileCount,
                string.Empty,
                folder.UsagePercent.ToString("0.####", CultureInfo.InvariantCulture),
                string.Empty);
        }

        foreach (var file in files)
        {
            AppendCsvRow(
                writer,
                "TopFile",
                resultState,
                _scanPath,
                file.Name,
                file.FullPath,
                file.SizeBytes,
                file.SizeDisplay,
                string.Empty,
                string.Empty,
                string.Empty,
                file.ExtensionDisplay);
        }

        return folderCount;
    }

    private static IEnumerable<ScanItem> FlattenFolders(IReadOnlyList<ScanItem> roots)
    {
        var pending = new Stack<ScanItem>();
        for (var index = roots.Count - 1; index >= 0; index--)
        {
            pending.Push(roots[index]);
        }

        while (pending.TryPop(out var item))
        {
            if (item.IsDirectory)
            {
                yield return item;
            }

            for (var index = item.Children.Count - 1; index >= 0; index--)
            {
                pending.Push(item.Children[index]);
            }
        }
    }

    private string BuildExportFileName()
    {
        var trimmedPath = _scanPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var targetName = Path.GetFileName(trimmedPath);
        if (string.IsNullOrWhiteSpace(targetName))
        {
            targetName = Path.GetPathRoot(_scanPath)?.TrimEnd(Path.DirectorySeparatorChar).TrimEnd(':') ?? "scan";
        }

        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            targetName = targetName.Replace(invalidCharacter, '-');
        }

        return $"ArcSpace-{targetName}-{DateTime.Now:yyyyMMdd-HHmm}.csv";
    }

    private static void AppendCsvRow(TextWriter writer, params object?[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0)
            {
                writer.Write(',');
            }

            var value = Convert.ToString(values[index], CultureInfo.InvariantCulture) ?? string.Empty;
            if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
            {
                value = "'" + value;
            }

            if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0)
            {
                writer.Write('"');
                writer.Write(value.Replace("\"", "\"\"", StringComparison.Ordinal));
                writer.Write('"');
            }
            else
            {
                writer.Write(value);
            }
        }

        writer.WriteLine();
    }
}

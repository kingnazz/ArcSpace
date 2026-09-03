using System.Diagnostics;
using System.IO;
using ArcSpace.Models;

namespace ArcSpace.Services;

public sealed class DiskScanner : IDiskScanner
{
    private const int LargestFileLimit = 100;
    private const int LiveFolderLimit = 40;
    private const int ProgressIntervalMilliseconds = 125;
    private const int SnapshotIntervalMilliseconds = 500;

    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
        BufferSize = 64 * 1024
    };

    private readonly Stopwatch _progressStopwatch = new();
    private readonly Stopwatch _snapshotStopwatch = new();
    private readonly List<DirectoryNode> _directories = [];
    private readonly List<DirectoryNode> _rootChildren = [];

    private long _filesScanned;
    private long _directoriesScanned;
    private long _skippedEntries;
    private long _bytesScanned;
    private PriorityQueue<ScanItem, long> _largestFiles = new();
    private DirectoryNode? _root;

    public Task<ScanResult> ScanAsync(
        string rootPath,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => Scan(rootPath, progress, cancellationToken));
    }

    private ScanResult Scan(
        string rootPath,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ResetCounters();

        var normalizedPath = Path.GetFullPath(rootPath);
        var root = CreateRoot(normalizedPath);
        _root = root;
        var pendingDirectories = new Stack<DirectoryNode>();
        pendingDirectories.Push(root);
        var wasCancelled = false;

        try
        {
            while (pendingDirectories.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ScanDirectory(directory, pendingDirectories, progress, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            wasCancelled = true;
        }

        FinalizeTree();
        var largestFiles = SnapshotLargestFiles();
        ReportProgress(progress, normalizedPath, force: true);

        return new ScanResult(
            root.Item,
            largestFiles,
            _skippedEntries,
            _directoriesScanned,
            wasCancelled);
    }

    private void ScanDirectory(
        DirectoryNode directory,
        Stack<DirectoryNode> pendingDirectories,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        _directoriesScanned++;
        var directoryInfo = new DirectoryInfo(directory.Item.FullPath);

        try
        {
            foreach (var entry in directoryInfo.EnumerateFileSystemInfos("*", EnumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        _skippedEntries++;
                        ReportProgress(progress, entry);
                        continue;
                    }

                    if (entry is DirectoryInfo childDirectory)
                    {
                        var child = CreateChild(directory, childDirectory);
                        directory.Item.Children.Add(child.Item);
                        _directories.Add(child);
                        if (directory.Parent is null)
                        {
                            _rootChildren.Add(child);
                        }

                        pendingDirectories.Push(child);
                        ReportProgress(progress, entry);
                        continue;
                    }

                    if (entry is not FileInfo file)
                    {
                        continue;
                    }

                    var size = file.Length;
                    directory.DirectFileBytes += size;
                    directory.DirectFileCount++;
                    _filesScanned++;
                    _bytesScanned += size;

                    if (directory.TopLevelBranch is { } branch)
                    {
                        branch.LiveBytes += size;
                        branch.LiveFileCount++;
                    }

                    TrackLargeFile(file, size);
                    ReportProgress(progress, entry);
                }
                catch (UnauthorizedAccessException)
                {
                    _skippedEntries++;
                }
                catch (IOException)
                {
                    _skippedEntries++;
                }
                catch (System.Security.SecurityException)
                {
                    _skippedEntries++;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            _skippedEntries++;
        }
        catch (IOException)
        {
            _skippedEntries++;
        }
        catch (System.Security.SecurityException)
        {
            _skippedEntries++;
        }

        ReportProgress(progress, directoryInfo.FullName);
    }

    private DirectoryNode CreateRoot(string normalizedPath)
    {
        var root = new DirectoryNode(
            new ScanItem
            {
                Name = GetDisplayName(normalizedPath),
                FullPath = normalizedPath,
                IsDirectory = true
            },
            parent: null);

        _directories.Add(root);
        return root;
    }

    private static DirectoryNode CreateChild(DirectoryNode parent, DirectoryInfo directory)
    {
        return new DirectoryNode(
            new ScanItem
            {
                Name = directory.Name,
                FullPath = directory.FullName,
                IsDirectory = true
            },
            parent);
    }

    private void FinalizeTree()
    {
        for (var index = _directories.Count - 1; index >= 0; index--)
        {
            var directory = _directories[index];
            directory.Item.Children.Sort(static (left, right) =>
            {
                var sizeComparison = right.SizeBytes.CompareTo(left.SizeBytes);
                return sizeComparison != 0
                    ? sizeComparison
                    : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            });

            directory.Item.SizeBytes = directory.DirectFileBytes + directory.ChildBytes;
            directory.Item.FileCount = directory.DirectFileCount + directory.ChildFileCount;

            if (directory.Parent is { } parent)
            {
                parent.ChildBytes += directory.Item.SizeBytes;
                parent.ChildFileCount += directory.Item.FileCount;
            }
        }
    }

    private void TrackLargeFile(FileInfo file, long size)
    {
        if (_largestFiles.Count >= LargestFileLimit)
        {
            if (!_largestFiles.TryPeek(out _, out var smallestSize) || size <= smallestSize)
            {
                return;
            }

            _largestFiles.Dequeue();
        }

        _largestFiles.Enqueue(new ScanItem
        {
            Name = file.Name,
            FullPath = file.FullName,
            SizeBytes = size,
            FileCount = 1,
            IsDirectory = false
        }, size);
    }

    private void ReportProgress(
        IProgress<ScanProgress>? progress,
        FileSystemInfo currentEntry)
    {
        if (progress is null || _progressStopwatch.ElapsedMilliseconds < ProgressIntervalMilliseconds)
        {
            return;
        }

        ReportProgressCore(progress, currentEntry.FullName);
    }

    private void ReportProgress(
        IProgress<ScanProgress>? progress,
        string currentPath,
        bool force = false)
    {
        if (progress is null || (!force && _progressStopwatch.ElapsedMilliseconds < ProgressIntervalMilliseconds))
        {
            return;
        }

        ReportProgressCore(progress, currentPath, force);
    }

    private void ReportProgressCore(
        IProgress<ScanProgress> progress,
        string currentPath,
        bool force = false)
    {
        ScanSnapshot? snapshot = null;
        if (force || _snapshotStopwatch.ElapsedMilliseconds >= SnapshotIntervalMilliseconds)
        {
            snapshot = new ScanSnapshot(SnapshotFolderHotspots(), SnapshotLargestFiles());
            _snapshotStopwatch.Restart();
        }

        progress.Report(new ScanProgress(
            _filesScanned,
            _directoriesScanned,
            _skippedEntries,
            _bytesScanned,
            currentPath,
            snapshot));

        _progressStopwatch.Restart();
    }

    private IReadOnlyList<FolderHotspot> SnapshotFolderHotspots()
    {
        if (_root is null)
        {
            return [];
        }

        if (_rootChildren.Count == 0)
        {
            if (_filesScanned == 0)
            {
                return [];
            }

            return
            [
                new FolderHotspot(
                    _root.Item.Name,
                    _root.Item.FullPath,
                    _bytesScanned,
                    _filesScanned)
            ];
        }

        var largestFolders = new PriorityQueue<DirectoryNode, long>();
        foreach (var directory in _rootChildren)
        {
            if (directory.LiveFileCount == 0 && directory.LiveBytes == 0)
            {
                continue;
            }

            if (largestFolders.Count < LiveFolderLimit)
            {
                largestFolders.Enqueue(directory, directory.LiveBytes);
                continue;
            }

            if (largestFolders.TryPeek(out _, out var smallestSize) && directory.LiveBytes > smallestSize)
            {
                largestFolders.Dequeue();
                largestFolders.Enqueue(directory, directory.LiveBytes);
            }
        }

        var hotspots = new List<FolderHotspot>(largestFolders.Count);
        foreach (var entry in largestFolders.UnorderedItems)
        {
            hotspots.Add(new FolderHotspot(
                entry.Element.Item.Name,
                entry.Element.Item.FullPath,
                entry.Element.LiveBytes,
                entry.Element.LiveFileCount));
        }

        hotspots.Sort(static (left, right) =>
        {
            var sizeComparison = right.SizeBytes.CompareTo(left.SizeBytes);
            return sizeComparison != 0
                ? sizeComparison
                : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        });
        return hotspots;
    }

    private IReadOnlyList<ScanItem> SnapshotLargestFiles()
    {
        var files = new List<ScanItem>(_largestFiles.Count);
        foreach (var entry in _largestFiles.UnorderedItems)
        {
            files.Add(entry.Element);
        }

        files.Sort(static (left, right) => right.SizeBytes.CompareTo(left.SizeBytes));
        return files;
    }

    private void ResetCounters()
    {
        _filesScanned = 0;
        _directoriesScanned = 0;
        _skippedEntries = 0;
        _bytesScanned = 0;
        _largestFiles = new PriorityQueue<ScanItem, long>();
        _directories.Clear();
        _rootChildren.Clear();
        _root = null;
        _progressStopwatch.Restart();
        _snapshotStopwatch.Restart();
    }

    private static string GetDisplayName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? Path.GetPathRoot(path) ?? path : name;
    }

    private sealed class DirectoryNode
    {
        public DirectoryNode(ScanItem item, DirectoryNode? parent)
        {
            Item = item;
            Parent = parent;
            TopLevelBranch = parent is null
                ? null
                : parent.Parent is null
                    ? this
                    : parent.TopLevelBranch;
        }

        public ScanItem Item { get; }
        public DirectoryNode? Parent { get; }
        public DirectoryNode? TopLevelBranch { get; }
        public long DirectFileBytes { get; set; }
        public long DirectFileCount { get; set; }
        public long ChildBytes { get; set; }
        public long ChildFileCount { get; set; }
        public long LiveBytes { get; set; }
        public long LiveFileCount { get; set; }
    }
}

public sealed record ScanResult(
    ScanItem Root,
    IReadOnlyList<ScanItem> LargestFiles,
    long SkippedEntries,
    long DirectoriesScanned,
    bool WasCancelled);

public sealed record ScanProgress(
    long FilesScanned,
    long DirectoriesScanned,
    long SkippedEntries,
    long BytesScanned,
    string CurrentPath,
    ScanSnapshot? Snapshot);

public sealed record ScanSnapshot(
    IReadOnlyList<FolderHotspot> FolderHotspots,
    IReadOnlyList<ScanItem> LargestFiles);

public sealed record FolderHotspot(
    string Name,
    string FullPath,
    long SizeBytes,
    long FileCount);

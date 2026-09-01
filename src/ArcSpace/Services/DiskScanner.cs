using ArcSpace.Models;

namespace ArcSpace.Services;

public sealed class DiskScanner
{
    private const int LargestFileLimit = 100;
    private long _filesScanned;
    private long _directoriesScanned;
    private long _skippedEntries;
    private long _bytesScanned;
    private PriorityQueue<ScanItem, long> _largestFiles = new();

    public async Task<ScanResult> ScanAsync(
        string rootPath,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            ResetCounters();

            var normalizedPath = Path.GetFullPath(rootPath);
            var root = ScanDirectory(normalizedPath, progress, cancellationToken);
            var largest = DrainLargestFiles();

            progress?.Report(new ScanProgress(
                _filesScanned,
                _directoriesScanned,
                _skippedEntries,
                _bytesScanned,
                normalizedPath));

            return new ScanResult(root, largest, _skippedEntries);
        }, cancellationToken);
    }

    private ScanItem ScanDirectory(
        string path,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _directoriesScanned++;

        var directoryItem = new ScanItem
        {
            Name = GetDisplayName(path),
            FullPath = path,
            IsDirectory = true,
            LastModified = SafeGetLastWriteTime(path)
        };

        var childDirectories = new List<ScanItem>();
        long directFileBytes = 0;
        long directFileCount = 0;

        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        _skippedEntries++;
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        var child = ScanDirectory(entry, progress, cancellationToken);
                        childDirectories.Add(child);
                        continue;
                    }

                    var info = new FileInfo(entry);
                    var size = info.Length;
                    directFileBytes += size;
                    directFileCount++;
                    _filesScanned++;
                    _bytesScanned += size;

                    TrackLargeFile(new ScanItem
                    {
                        Name = info.Name,
                        FullPath = info.FullName,
                        SizeBytes = size,
                        FileCount = 1,
                        IsDirectory = false,
                        LastModified = info.LastWriteTime
                    });

                    if ((_filesScanned & 511) == 0)
                    {
                        progress?.Report(new ScanProgress(
                            _filesScanned,
                            _directoriesScanned,
                            _skippedEntries,
                            _bytesScanned,
                            entry));
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

        foreach (var child in childDirectories.OrderByDescending(x => x.SizeBytes))
        {
            directoryItem.Children.Add(child);
        }

        directoryItem.SizeBytes = directFileBytes + childDirectories.Sum(x => x.SizeBytes);
        directoryItem.FileCount = directFileCount + childDirectories.Sum(x => x.FileCount);

        if ((_directoriesScanned & 127) == 0)
        {
            progress?.Report(new ScanProgress(
                _filesScanned,
                _directoriesScanned,
                _skippedEntries,
                _bytesScanned,
                path));
        }

        return directoryItem;
    }

    private void TrackLargeFile(ScanItem item)
    {
        if (_largestFiles.Count < LargestFileLimit)
        {
            _largestFiles.Enqueue(item, item.SizeBytes);
            return;
        }

        if (_largestFiles.TryPeek(out _, out var smallestSize) && item.SizeBytes > smallestSize)
        {
            _largestFiles.Dequeue();
            _largestFiles.Enqueue(item, item.SizeBytes);
        }
    }

    private IReadOnlyList<ScanItem> DrainLargestFiles()
    {
        var files = new List<ScanItem>(_largestFiles.Count);
        while (_largestFiles.TryDequeue(out var item, out _))
        {
            files.Add(item);
        }

        files.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        return files;
    }

    private void ResetCounters()
    {
        _filesScanned = 0;
        _directoriesScanned = 0;
        _skippedEntries = 0;
        _bytesScanned = 0;
        _largestFiles = new PriorityQueue<ScanItem, long>();
    }

    private static string GetDisplayName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? Path.GetPathRoot(path) ?? path : name;
    }

    private static DateTime SafeGetLastWriteTime(string path)
    {
        try
        {
            return Directory.GetLastWriteTime(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }
}

public sealed record ScanResult(
    ScanItem Root,
    IReadOnlyList<ScanItem> LargestFiles,
    long SkippedEntries);

public sealed record ScanProgress(
    long FilesScanned,
    long DirectoriesScanned,
    long SkippedEntries,
    long BytesScanned,
    string CurrentPath);

using System.Diagnostics;
using System.IO;
using ArcSpace.Models;

namespace ArcSpace.Services;

public sealed class DiskScanner
{
    private const int LargestFileLimit = 100;
    private const int ProgressIntervalMilliseconds = 200;

    private long _filesScanned;
    private long _directoriesScanned;
    private long _skippedEntries;
    private long _bytesScanned;
    private PriorityQueue<ScanItem, long> _largestFiles = new();
    private readonly Stopwatch _progressStopwatch = new();

    public async Task<ScanResult> ScanAsync(
        string rootPath,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            ResetCounters();

            var normalizedPath = Path.GetFullPath(rootPath);
            var root = ScanDirectory(new DirectoryInfo(normalizedPath), progress, cancellationToken);
            var largest = SnapshotLargestFiles();

            ReportProgress(progress, normalizedPath, force: true);

            return new ScanResult(root, largest, _skippedEntries);
        }, cancellationToken);
    }

    private ScanItem ScanDirectory(
        DirectoryInfo directory,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _directoriesScanned++;

        var directoryItem = new ScanItem
        {
            Name = GetDisplayName(directory.FullName),
            FullPath = directory.FullName,
            IsDirectory = true
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
                AttributesToSkip = FileAttributes.ReparsePoint,
                BufferSize = 64 * 1024
            };

            foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (entry is DirectoryInfo childDirectory)
                    {
                        childDirectories.Add(ScanDirectory(childDirectory, progress, cancellationToken));
                        continue;
                    }

                    if (entry is not FileInfo file)
                    {
                        continue;
                    }

                    var size = file.Length;
                    directFileBytes += size;
                    directFileCount++;
                    _filesScanned++;
                    _bytesScanned += size;

                    TrackLargeFile(file, size);
                    ReportProgress(progress, file.FullName);
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

        childDirectories.Sort(static (a, b) => b.SizeBytes.CompareTo(a.SizeBytes));

        long childBytes = 0;
        long childFileCount = 0;
        foreach (var child in childDirectories)
        {
            childBytes += child.SizeBytes;
            childFileCount += child.FileCount;
            directoryItem.Children.Add(child);
        }

        directoryItem.SizeBytes = directFileBytes + childBytes;
        directoryItem.FileCount = directFileCount + childFileCount;

        ReportProgress(progress, directory.FullName);
        return directoryItem;
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
            IsDirectory = false,
            LastModified = SafeGetLastWriteTime(file)
        }, size);
    }

    private void ReportProgress(IProgress<ScanProgress>? progress, string currentPath, bool force = false)
    {
        if (progress is null)
        {
            return;
        }

        if (!force && _progressStopwatch.ElapsedMilliseconds < ProgressIntervalMilliseconds)
        {
            return;
        }

        progress.Report(new ScanProgress(
            _filesScanned,
            _directoriesScanned,
            _skippedEntries,
            _bytesScanned,
            currentPath,
            SnapshotLargestFiles()));

        _progressStopwatch.Restart();
    }

    private IReadOnlyList<ScanItem> SnapshotLargestFiles()
        => _largestFiles.UnorderedItems
            .Select(static item => item.Element)
            .OrderByDescending(static item => item.SizeBytes)
            .ToArray();

    private void ResetCounters()
    {
        _filesScanned = 0;
        _directoriesScanned = 0;
        _skippedEntries = 0;
        _bytesScanned = 0;
        _largestFiles = new PriorityQueue<ScanItem, long>();
        _progressStopwatch.Restart();
    }

    private static string GetDisplayName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? Path.GetPathRoot(path) ?? path : name;
    }

    private static DateTime SafeGetLastWriteTime(FileInfo file)
    {
        try
        {
            return file.LastWriteTime;
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
    string CurrentPath,
    IReadOnlyList<ScanItem> LargestFiles);

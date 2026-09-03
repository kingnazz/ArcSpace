using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ArcSpace.Models;

public sealed class ScanItem : INotifyPropertyChanged
{
    private long _sizeBytes;
    private long _fileCount;

    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public List<ScanItem> Children { get; } = [];

    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (_sizeBytes == value)
            {
                return;
            }

            _sizeBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SizeDisplay));
        }
    }

    public long FileCount
    {
        get => _fileCount;
        set
        {
            if (_fileCount == value)
            {
                return;
            }

            _fileCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FileCountDisplay));
        }
    }

    public string SizeDisplay => FormatBytes(SizeBytes);
    public string FileCountDisplay => FileCount.ToString("N0");

    public event PropertyChangedEventHandler? PropertyChanged;

    public double PercentOf(long parentSize)
        => parentSize <= 0 ? 0 : (double)SizeBytes / parentSize * 100d;

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = Math.Max(0, bytes);
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

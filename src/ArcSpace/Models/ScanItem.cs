namespace ArcSpace.Models;

public sealed class ScanItem
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public long SizeBytes { get; set; }
    public long FileCount { get; set; }
    public DateTime LastModified { get; init; }
    public bool IsDirectory { get; init; }
    public List<ScanItem> Children { get; } = [];

    public string SizeDisplay => FormatBytes(SizeBytes);
    public string FileCountDisplay => FileCount.ToString("N0");
    public string LastModifiedDisplay => LastModified == DateTime.MinValue ? string.Empty : LastModified.ToString("g");

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
}

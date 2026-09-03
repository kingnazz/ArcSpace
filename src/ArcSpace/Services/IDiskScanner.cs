namespace ArcSpace.Services;

public interface IDiskScanner
{
    Task<ScanResult> ScanAsync(
        string rootPath,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}

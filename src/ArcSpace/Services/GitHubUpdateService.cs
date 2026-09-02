using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArcSpace.Services;

public sealed class GitHubUpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/kingnazz/ArcSpace/releases/latest";
    private const string ReleaseAssetName = "ArcSpace.exe";
    private readonly HttpClient _httpClient;

    public GitHubUpdateService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ArcSpace", CurrentVersion.ToString()));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(LatestReleaseUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<ReleaseResponse>(stream, cancellationToken: cancellationToken);
        if (release is null || release.Draft || release.Prerelease)
        {
            return null;
        }

        if (!TryParseVersion(release.TagName, out var releaseVersion) || releaseVersion <= CurrentVersion)
        {
            return null;
        }

        var asset = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, ReleaseAssetName, StringComparison.OrdinalIgnoreCase));

        if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
        {
            return null;
        }

        return new UpdateInfo(releaseVersion, release.TagName, release.HtmlUrl, asset.BrowserDownloadUrl);
    }

    public async Task DownloadAndStageUpdateAsync(UpdateInfo update, CancellationToken cancellationToken = default)
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
        {
            throw new InvalidOperationException("ArcSpace could not determine the running executable path.");
        }

        var updateDirectory = Path.Combine(Path.GetTempPath(), "ArcSpaceUpdate");
        Directory.CreateDirectory(updateDirectory);
        var downloadedExe = Path.Combine(updateDirectory, $"ArcSpace-{update.Version}.exe");

        using (var response = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(downloadedExe, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination, cancellationToken);
        }

        var stagedInfo = new FileInfo(downloadedExe);
        if (!stagedInfo.Exists || stagedInfo.Length < 1_000_000)
        {
            throw new InvalidDataException("The downloaded ArcSpace update is incomplete.");
        }

        StartReplacementScript(currentExe, downloadedExe);
    }

    private static void StartReplacementScript(string currentExe, string downloadedExe)
    {
        var updateDirectory = Path.GetDirectoryName(downloadedExe)!;
        var scriptPath = Path.Combine(updateDirectory, "apply-update.cmd");
        var pid = Environment.ProcessId;

        var script = $"""
@echo off
setlocal
:waitloop
tasklist /FI "PID eq {pid}" 2>NUL | find "{pid}" >NUL
if not errorlevel 1 (
  timeout /t 1 /nobreak >NUL
  goto waitloop
)
copy /Y "{downloadedExe}" "{currentExe}" >NUL
if errorlevel 1 exit /b 1
start "" "{currentExe}"
del /Q "{downloadedExe}" >NUL 2>&1
del /Q "%~f0" >NUL 2>&1
""";

        File.WriteAllText(scriptPath, script);
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c start \"ArcSpace Update\" /min \"{scriptPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        var normalized = tag.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        return Version.TryParse(normalized, out version!);
    }

    private sealed class ReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = string.Empty;

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("assets")]
        public List<ReleaseAsset> Assets { get; init; } = [];
    }

    private sealed class ReleaseAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;
    }
}

public sealed record UpdateInfo(Version Version, string TagName, string ReleasePageUrl, string DownloadUrl);

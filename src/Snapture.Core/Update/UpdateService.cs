using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snapture.Core.Update;

/// <summary>One published release, as listed in the update manifest.</summary>
public sealed class ReleaseInfo
{
    public string Version { get; set; } = "0.0.0";
    public string? Date { get; set; }
    public string? Notes { get; set; }

    /// <summary>Absolute URL of the installer (.exe) for this release.</summary>
    public string InstallerUrl { get; set; } = string.Empty;

    /// <summary>Optional lowercase hex SHA-256 of the installer, verified after download.</summary>
    public string? Sha256 { get; set; }

    public System.Version SemVer =>
        System.Version.TryParse(Version, out var v) ? v : new System.Version(0, 0, 0, 0);
}

/// <summary>The <c>releases.json</c> document hosted on the GitHub Pages branch.</summary>
public sealed class UpdateManifest
{
    public string? Product { get; set; }
    public List<ReleaseInfo> Releases { get; set; } = new();
}

/// <summary>
/// Checks a static JSON manifest (hosted on the project's GitHub Pages branch)
/// for a newer release and downloads its installer. Deliberately dependency-free:
/// the manifest is plain JSON and the installer is a normal file download, so the
/// whole update feed is just static files on Pages.
/// </summary>
public sealed class UpdateService
{
    /// <summary>Where the release manifest lives. Adjust if the Pages path changes.</summary>
    public const string ManifestUrl = "https://este2013.github.io/TOOLS/snapture/releases.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly string _manifestUrl;

    public UpdateService(System.Version currentVersion, string? manifestUrl = null)
    {
        CurrentVersion = currentVersion;
        _manifestUrl = manifestUrl ?? ManifestUrl;
    }

    public System.Version CurrentVersion { get; }

    /// <summary>Fetch the manifest, or null if unreachable/unparseable.</summary>
    public async Task<UpdateManifest?> FetchManifestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Bust caches so a fresh publish is seen promptly.
            var url = _manifestUrl + (_manifestUrl.Contains('?') ? "&" : "?") + "t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var json = await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The newest release strictly greater than the current version, if any.</summary>
    public ReleaseInfo? FindNewer(UpdateManifest manifest) =>
        manifest.Releases
            .Where(r => !string.IsNullOrWhiteSpace(r.InstallerUrl) && r.SemVer > CurrentVersion)
            .OrderByDescending(r => r.SemVer)
            .FirstOrDefault();

    /// <summary>
    /// Download the installer to a temp file, verifying its SHA-256 when the
    /// manifest provides one. Returns the local path.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(
        ReleaseInfo release, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var fileName = SafeFileName(release);
        var path = Path.Combine(Path.GetTempPath(), fileName);

        using (var response = await Http.GetAsync(release.InstallerUrl,
                   HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1L;

            await using var src = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                read += n;
                if (total > 0)
                    progress?.Report((double)read / total);
            }
        }

        if (!string.IsNullOrWhiteSpace(release.Sha256))
        {
            var actual = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
            if (!actual.Equals(release.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(path); } catch { }
                throw new InvalidOperationException("Downloaded installer failed its integrity check.");
            }
        }

        return path;
    }

    private static string SafeFileName(ReleaseInfo release)
    {
        var fromUrl = Path.GetFileName(new Uri(release.InstallerUrl).AbsolutePath);
        if (!string.IsNullOrWhiteSpace(fromUrl) && fromUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return fromUrl;
        return $"Snapture-Setup-{release.Version}.exe";
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

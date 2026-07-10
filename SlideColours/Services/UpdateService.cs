using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace SlideColours.Services;

/// <summary>Details of a newer release found on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, string ReleaseUrl);

/// <summary>
/// Checks GitHub Releases for a newer build and, when asked, replaces the running executable in
/// place. The current exe is renamed to a versioned backup (leaving it alongside the app) and the
/// freshly downloaded exe takes its name — a trick that works because Windows lets a running image
/// be renamed within its own directory.
/// </summary>
public sealed class UpdateService
{
    private const string Owner = "tango7nz";
    private const string Repo = "slide-colours";
    private static readonly string LatestReleaseUrl =
        $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>The running version, taken from the assembly (set by &lt;Version&gt; in the csproj).</summary>
    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>The most recently found update, or null when up to date / not yet checked.</summary>
    public UpdateInfo? Available { get; private set; }

    /// <summary>Raised when a check discovers an update. May fire on a thread-pool thread.</summary>
    public event Action<UpdateInfo>? UpdateAvailable;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub rejects API requests that don't send a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SlideColours-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>
    /// Queries the latest release. Returns update details when the published version is newer than
    /// the running one; null when up to date or on any failure (offline, rate-limited, malformed).
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            await using var stream = await Http.GetStreamAsync(LatestReleaseUrl, ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!TryParseTagVersion(tag, out var latest) || latest <= CurrentVersion)
            {
                Available = null;
                return null;
            }

            // Find the Windows .exe asset to download.
            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }
            if (downloadUrl == null)
                return null;

            string releaseUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            var info = new UpdateInfo(latest, tag, downloadUrl, releaseUrl);
            Available = info;
            UpdateAvailable?.Invoke(info);
            return info;
        }
        catch
        {
            // Any failure means "no update" — updating is best-effort and must never crash the app.
            return null;
        }
    }

    /// <summary>Tags look like "v1.2.3"; strip a leading v/V before parsing.</summary>
    private static bool TryParseTagVersion(string tag, out Version version) =>
        Version.TryParse(tag.TrimStart('v', 'V').Trim(), out version!);

    /// <summary>
    /// Downloads the new exe next to the running one, renames the running file to a versioned
    /// backup, swaps the new file into its place and launches it. The caller should then exit.
    /// </summary>
    public async Task DownloadAndApplyAsync(UpdateInfo info, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        string currentPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the running executable path.");
        string dir = Path.GetDirectoryName(currentPath)!;
        string nameNoExt = Path.GetFileNameWithoutExtension(currentPath);
        string ext = Path.GetExtension(currentPath);

        string newPath = Path.Combine(dir, $"{nameNoExt}.new{ext}");
        string backupPath = Path.Combine(dir, $"{nameNoExt}-{CurrentVersion.ToString(3)}-backup{ext}");

        // 1. Download the replacement alongside the current exe.
        await DownloadToFileAsync(info.DownloadUrl, newPath, progress, ct);

        // 2. Move the running exe aside — Windows allows renaming a running image in place. This is
        //    also the copy of the replaced version we leave behind for the user.
        if (File.Exists(backupPath))
            File.Delete(backupPath);
        File.Move(currentPath, backupPath);

        // 3. Put the freshly downloaded exe where the old one was.
        try
        {
            File.Move(newPath, currentPath);
        }
        catch
        {
            // Swap failed — restore the original so the app is still runnable.
            File.Move(backupPath, currentPath);
            throw;
        }

        // 4. Launch the new version. The caller shuts the current instance down afterwards.
        Process.Start(new ProcessStartInfo(currentPath) { UseShellExecute = true });
    }

    private static async Task DownloadToFileAsync(string url, string path,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total is > 0)
                progress?.Report((double)read / total.Value);
        }
    }
}

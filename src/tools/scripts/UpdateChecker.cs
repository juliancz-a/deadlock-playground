using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HttpClient = System.Net.Http.HttpClient;

namespace DeadlockPlayground.Tools;

/// <summary>
/// Result data model for a GitHub release update check.
/// </summary>
public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string RemoteVersionTag,
    string RemoteVersionClean,
    string LocalVersion,
    string ReleaseUrl,
    string DirectZipUrl = ""
);

/// <summary>
/// Asynchronous utility that queries GitHub Releases for the latest version of Deadlock Playground,
/// downloads release archives, and applies atomic in-app updates using the Windows .bak file rename strategy.
/// </summary>
public static class UpdateChecker
{
    public const string DefaultRepoOwner = "juliancz-a";
    public const string DefaultRepoName = "deadlock-playground";
    public const string FallbackVersion = "1.0.0";
    public const string UserAgentHeader = "DeadlockPlayground-UpdateChecker";

    private static readonly HttpClient _apiHttpClient = CreateApiHttpClient();
    private static readonly HttpClient _downloadHttpClient = CreateDownloadHttpClient();

    /// <summary>
    /// Indicates whether the current application session was launched immediately following an update cleanup.
    /// </summary>
    public static bool IsPostUpdateLaunch { get; private set; }

    private static HttpClient CreateApiHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgentHeader);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");
        return client;
    }

    private static HttpClient CreateDownloadHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgentHeader);
        return client;
    }

    /// <summary>
    /// Reads the configured application version from Godot ProjectSettings,
    /// falling back to <see cref="FallbackVersion"/> if unconfigured or empty.
    /// </summary>
    public static string GetLocalVersion()
    {
        try
        {
            if (ProjectSettings.HasSetting("application/config/version"))
            {
                string configuredVersion = ProjectSettings.GetSetting("application/config/version").AsString();
                if (!string.IsNullOrWhiteSpace(configuredVersion))
                {
                    return configuredVersion.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            GD.Print($"[UpdateChecker] Could not read ProjectSettings version: {ex.Message}");
        }

        return FallbackVersion;
    }

    /// <summary>
    /// Robustly parses a version string (e.g. "1.2.0", "v1.2.3", "1.0-beta") into a normalized <see cref="Version"/>.
    /// </summary>
    public static bool TryParseVersion(string versionString, out Version version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(versionString))
        {
            return false;
        }

        // Sanitize leading 'v' or 'V' and trim whitespace
        string sanitized = versionString.Trim().TrimStart('v', 'V').Trim();

        // Isolate numeric version core if pre-release or build metadata exists (e.g. 1.2.0-rc1+build100)
        int hyphenIdx = sanitized.IndexOf('-');
        if (hyphenIdx >= 0)
        {
            sanitized = sanitized.Substring(0, hyphenIdx);
        }

        int plusIdx = sanitized.IndexOf('+');
        if (plusIdx >= 0)
        {
            sanitized = sanitized.Substring(0, plusIdx);
        }

        if (!Version.TryParse(sanitized, out var parsed))
        {
            return false;
        }

        // Normalize to 4 components (Major, Minor, Build, Revision) to avoid -1 comparison quirks
        version = new Version(
            Math.Max(0, parsed.Major),
            Math.Max(0, parsed.Minor),
            Math.Max(0, parsed.Build),
            Math.Max(0, parsed.Revision)
        );

        return true;
    }

    /// <summary>
    /// Compares a remote version tag against a local version string.
    /// Returns true only if remoteVersion > localVersion.
    /// </summary>
    public static bool IsRemoteVersionNewer(string remoteTag, string localVersionString)
    {
        if (TryParseVersion(remoteTag, out var remoteVer) && TryParseVersion(localVersionString, out var localVer))
        {
            return remoteVer > localVer;
        }
        return false;
    }

    /// <summary>
    /// Queries the GitHub Release endpoint asynchronously, extracts the latest release information,
    /// parses the assets array for a Windows release zip, and determines whether an update is available.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(
        string repoOwner = DefaultRepoOwner,
        string repoName = DefaultRepoName,
        CancellationToken cancellationToken = default)
    {
        string localVersion = GetLocalVersion();

        try
        {
            string url = $"https://api.github.com/repos/{repoOwner}/{repoName}/releases/latest";
            using var response = await _apiHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                GD.Print($"[UpdateChecker] GitHub API responded with status {response.StatusCode} for {url}");
                return new UpdateCheckResult(
                    IsUpdateAvailable: false,
                    RemoteVersionTag: string.Empty,
                    RemoteVersionClean: string.Empty,
                    LocalVersion: localVersion,
                    ReleaseUrl: string.Empty,
                    DirectZipUrl: string.Empty
                );
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var jsonDoc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = jsonDoc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? string.Empty : string.Empty;
            string htmlUrl = root.TryGetProperty("html_url", out var urlElem) ? urlElem.GetString() ?? string.Empty : string.Empty;

            string directZipUrl = string.Empty;
            if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
            {
                string candidateUrl = string.Empty;
                int bestScore = -1;

                foreach (var asset in assetsElem.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var nameProp) ? (nameProp.GetString() ?? string.Empty) : string.Empty;
                    string dlUrl = asset.TryGetProperty("browser_download_url", out var dlProp) ? (dlProp.GetString() ?? string.Empty) : string.Empty;

                    if (string.IsNullOrWhiteSpace(dlUrl) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int score = 0;
                    string lower = name.ToLowerInvariant();
                    if (lower.Contains("win") || lower.Contains("windows")) score += 10;
                    if (lower.Contains("x86_64") || lower.Contains("x64")) score += 5;
                    if (lower.Contains("deadlock")) score += 2;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        candidateUrl = dlUrl;
                    }
                }

                directZipUrl = candidateUrl;
            }

            string remoteVersionClean = tagName.Trim().TrimStart('v', 'V').Trim();
            bool isNewer = IsRemoteVersionNewer(remoteVersionClean, localVersion);

            return new UpdateCheckResult(
                IsUpdateAvailable: isNewer,
                RemoteVersionTag: tagName,
                RemoteVersionClean: remoteVersionClean,
                LocalVersion: localVersion,
                ReleaseUrl: htmlUrl,
                DirectZipUrl: directZipUrl
            );
        }
        catch (Exception ex)
        {
            // Fail silently on timeout, offline status, or rate limiting
            GD.Print($"[UpdateChecker] Update check skipped or failed: {ex.Message}");
            return new UpdateCheckResult(
                IsUpdateAvailable: false,
                RemoteVersionTag: string.Empty,
                RemoteVersionClean: string.Empty,
                LocalVersion: localVersion,
                ReleaseUrl: string.Empty,
                DirectZipUrl: string.Empty
            );
        }
    }

    /// <summary>
    /// Asynchronously streams download of the release zip archive into the user data directory,
    /// extracts it to a staging folder, and verifies that the payload contains the application executable.
    /// </summary>
    public static async Task<bool> DownloadAndPrepareUpdateAsync(
        string zipUrl,
        Action<float, string> onProgress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(zipUrl))
        {
            GD.PrintErr("[UpdateChecker] Download failed: zipUrl is null or empty.");
            return false;
        }

        try
        {
            string userDataDir = OS.GetUserDataDir();
            Directory.CreateDirectory(userDataDir);

            string tempZipPath = Path.Combine(userDataDir, "deadlock_update.zip");
            string stagingDir = Path.Combine(userDataDir, "update_staging");

            if (File.Exists(tempZipPath))
            {
                try { File.Delete(tempZipPath); } catch { }
            }

            onProgress?.Invoke(0f, "Connecting to download server...");
            using var response = await _downloadHttpClient.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? -1L;
            GD.Print($"[UpdateChecker] Starting download of {zipUrl} (Total size: {totalBytes} bytes)...");

            await using (var fileStream = new FileStream(tempZipPath, FileMode.Create, System.IO.FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true))
            await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                byte[] buffer = new byte[81920];
                long bytesReadTotal = 0;
                int bytesRead;
                var lastProgressTime = DateTime.UtcNow;

                while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                    bytesReadTotal += bytesRead;

                    var now = DateTime.UtcNow;
                    if ((now - lastProgressTime).TotalMilliseconds >= 80 || (totalBytes > 0 && bytesReadTotal == totalBytes))
                    {
                        lastProgressTime = now;
                        float progress = totalBytes > 0 ? (float)bytesReadTotal / totalBytes : -1f;
                        string statusText;
                        if (totalBytes > 0)
                        {
                            float mbCurrent = bytesReadTotal / (1024f * 1024f);
                            float mbTotal = totalBytes / (1024f * 1024f);
                            int percent = Math.Clamp((int)(progress * 100f), 0, 100);
                            statusText = $"Downloading update... {percent}% ({mbCurrent:F1} / {mbTotal:F1} MB)";
                        }
                        else
                        {
                            float mbCurrent = bytesReadTotal / (1024f * 1024f);
                            statusText = $"Downloading update... ({mbCurrent:F1} MB)";
                        }
                        onProgress?.Invoke(progress, statusText);
                    }
                }
            }

            onProgress?.Invoke(1.0f, "Extracting update files...");

            if (Directory.Exists(stagingDir))
            {
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
            }
            Directory.CreateDirectory(stagingDir);

            await Task.Run(() =>
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(tempZipPath, stagingDir, overwriteFiles: true);
            }, cancellationToken).ConfigureAwait(false);

            string effectiveStaging = GetEffectiveStagingDirectory();
            if (string.IsNullOrEmpty(effectiveStaging) || !Directory.Exists(effectiveStaging))
            {
                GD.PrintErr($"[UpdateChecker] Staging directory not found: {effectiveStaging}");
                return false;
            }

            var exeFiles = Directory.GetFiles(effectiveStaging, "*.exe", SearchOption.AllDirectories);
            if (exeFiles.Length == 0)
            {
                GD.PrintErr($"[UpdateChecker] Staged payload does not contain an executable: {effectiveStaging}");
                return false;
            }

            GD.Print($"[UpdateChecker] Update successfully staged at: {effectiveStaging} (found {exeFiles.Length} executable(s)).");
            onProgress?.Invoke(1.0f, "Update ready to install.");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[UpdateChecker] Download or extraction failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Resolves the actual payload directory inside update_staging, accounting for single wrapper folders.
    /// </summary>
    public static string GetEffectiveStagingDirectory()
    {
        string stagingDir = Path.Combine(OS.GetUserDataDir(), "update_staging");
        if (!Directory.Exists(stagingDir)) return string.Empty;

        var dirs = Directory.GetDirectories(stagingDir);
        var files = Directory.GetFiles(stagingDir);

        if (files.Length == 0 && dirs.Length == 1)
        {
            return dirs[0];
        }

        return stagingDir;
    }

    /// <summary>
    /// Applies the staged update using the atomic Windows .bak file rename strategy,
    /// launches the updated executable with the --cleanup-update flag, and terminates the running process.
    /// </summary>
    public static void ApplyUpdateAndRestart()
    {
        if (OS.HasFeature("editor"))
        {
            GD.PrintErr("[UpdateChecker] Auto-update replacement is disabled while running in the Godot Editor.");
            return;
        }

        try
        {
            string currentExe = OS.GetExecutablePath();
            string baseDir = Path.GetDirectoryName(currentExe) ?? currentExe.GetBaseDir();
            string effectiveStaging = GetEffectiveStagingDirectory();

            if (string.IsNullOrEmpty(effectiveStaging) || !Directory.Exists(effectiveStaging))
            {
                GD.PrintErr($"[UpdateChecker] Cannot apply update: Staging directory not found: {effectiveStaging}");
                return;
            }

            GD.Print($"[UpdateChecker] Applying atomic update from '{effectiveStaging}' to '{baseDir}'...");

            // 1. Rename active executable (.bak swap)
            string currentExeBak = currentExe + ".bak";
            if (File.Exists(currentExeBak))
            {
                try { File.Delete(currentExeBak); } catch { }
            }
            File.Move(currentExe, currentExeBak, overwrite: true);
            GD.Print($"[UpdateChecker] Renamed active executable: {currentExe} -> {currentExeBak}");

            // 2. Rename active standalone .pck if present
            string currentPck = Path.ChangeExtension(currentExe, ".pck");
            if (File.Exists(currentPck))
            {
                string currentPckBak = currentPck + ".bak";
                if (File.Exists(currentPckBak))
                {
                    try { File.Delete(currentPckBak); } catch { }
                }
                File.Move(currentPck, currentPckBak, overwrite: true);
                GD.Print($"[UpdateChecker] Renamed active .pck: {currentPck} -> {currentPckBak}");
            }

            // 3. Move/overwrite newly extracted files and assemblies from staging to baseDir
            CopyDirectoryRecursiveWithBakFallback(effectiveStaging, baseDir);

            // Ensure the target executable name exists if the staged binary was named differently
            string currentExeName = Path.GetFileName(currentExe);
            string destExePath = Path.Combine(baseDir, currentExeName);
            if (!File.Exists(destExePath))
            {
                var baseExes = Directory.GetFiles(baseDir, "*.exe");
                if (baseExes.Length > 0 && !baseExes[0].EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        File.Copy(baseExes[0], destExePath, overwrite: true);
                    }
                    catch { }
                }
            }

            GD.Print("[UpdateChecker] Successfully swapped and copied update payload.");

            // 4. Launch updated process and terminate current instance cleanly
            string[] launchArgs = new string[] { "--cleanup-update" };
            int pid = OS.CreateProcess(currentExe, launchArgs);
            GD.Print($"[UpdateChecker] Spawned updated process with PID {pid}. Quitting current application...");

            if (Engine.GetMainLoop() is SceneTree tree)
            {
                tree.Quit();
            }
            else
            {
                System.Environment.Exit(0);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[UpdateChecker] Failed to apply update and restart: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// Recursively copies files and subdirectories from sourceDir to targetDir.
    /// If a target file is locked by the active process, it renames the target to .bak before copying.
    /// </summary>
    private static void CopyDirectoryRecursiveWithBakFallback(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string fileName = Path.GetFileName(file);
            string destFile = Path.Combine(targetDir, fileName);

            try
            {
                File.Copy(file, destFile, overwrite: true);
            }
            catch
            {
                // Destination file may be locked by current process.
                // Move destination to .bak first, then copy.
                string bakFile = destFile + ".bak";
                try { if (File.Exists(bakFile)) File.Delete(bakFile); } catch { }
                try
                {
                    File.Move(destFile, bakFile, overwrite: true);
                    File.Copy(file, destFile, overwrite: true);
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[UpdateChecker] Failed copying '{file}' to '{destFile}': {ex.Message}");
                }
            }
        }

        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string dirName = Path.GetFileName(subDir);
            string destSubDir = Path.Combine(targetDir, dirName);
            CopyDirectoryRecursiveWithBakFallback(subDir, destSubDir);
        }
    }

    /// <summary>
    /// Inspects command line arguments for the --cleanup-update flag.
    /// If present, removes all leftover *.bak files in the application directory
    /// and cleans the temporary update staging area.
    /// </summary>
    public static bool CleanupPostUpdate()
    {
        try
        {
            string[] cmdArgs = OS.GetCmdlineArgs();
            bool hasCleanupFlag = false;
            foreach (string arg in cmdArgs)
            {
                if (arg.Equals("--cleanup-update", StringComparison.OrdinalIgnoreCase))
                {
                    hasCleanupFlag = true;
                    break;
                }
            }

            if (!hasCleanupFlag)
            {
                return false;
            }

            IsPostUpdateLaunch = true;
            GD.Print("[UpdateChecker] --cleanup-update flag detected. Running post-update cleanup routine...");

            string currentExe = OS.GetExecutablePath();
            string baseDir = Path.GetDirectoryName(currentExe) ?? currentExe.GetBaseDir();

            if (Directory.Exists(baseDir))
            {
                var bakFiles = Directory.GetFiles(baseDir, "*.bak", SearchOption.AllDirectories);
                foreach (string bakFile in bakFiles)
                {
                    try
                    {
                        File.Delete(bakFile);
                        GD.Print($"[UpdateChecker] Deleted backup file: {bakFile}");
                    }
                    catch (Exception ex)
                    {
                        GD.Print($"[UpdateChecker] Could not delete {bakFile}: {ex.Message}");
                    }
                }
            }

            // Clean user data temporary download and staging files
            string userDataDir = OS.GetUserDataDir();
            string tempZip = Path.Combine(userDataDir, "deadlock_update.zip");
            if (File.Exists(tempZip))
            {
                try { File.Delete(tempZip); } catch { }
            }

            string stagingDir = Path.Combine(userDataDir, "update_staging");
            if (Directory.Exists(stagingDir))
            {
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
            }

            GD.Print("[UpdateChecker] Post-update cleanup completed successfully.");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[UpdateChecker] Error in CleanupPostUpdate: {ex.Message}");
            return false;
        }
    }
}

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
    string ReleaseUrl
);

/// <summary>
/// Asynchronous utility that queries GitHub Releases for the latest version of Deadlock Playground,
/// compares it against the local project configuration version, and handles network or parse failures gracefully.
/// </summary>
public static class UpdateChecker
{
    public const string DefaultRepoOwner = "juliancz-a";
    public const string DefaultRepoName = "deadlock-playground";
    public const string FallbackVersion = "1.0.0";
    public const string UserAgentHeader = "DeadlockPlayground-UpdateChecker";

    private static readonly HttpClient _httpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgentHeader);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");
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
    /// and determines whether an update is available.
    /// Wrapped in a try/catch block to fail silently on network issues, timeouts, or rate limits.
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
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                GD.Print($"[UpdateChecker] GitHub API responded with status {response.StatusCode} for {url}");
                return new UpdateCheckResult(
                    IsUpdateAvailable: false,
                    RemoteVersionTag: string.Empty,
                    RemoteVersionClean: string.Empty,
                    LocalVersion: localVersion,
                    ReleaseUrl: string.Empty
                );
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var jsonDoc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = jsonDoc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? string.Empty : string.Empty;
            string htmlUrl = root.TryGetProperty("html_url", out var urlElem) ? urlElem.GetString() ?? string.Empty : string.Empty;

            string remoteVersionClean = tagName.Trim().TrimStart('v', 'V').Trim();
            bool isNewer = IsRemoteVersionNewer(remoteVersionClean, localVersion);

            return new UpdateCheckResult(
                IsUpdateAvailable: isNewer,
                RemoteVersionTag: tagName,
                RemoteVersionClean: remoteVersionClean,
                LocalVersion: localVersion,
                ReleaseUrl: htmlUrl
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
                ReleaseUrl: string.Empty
            );
        }
    }
}

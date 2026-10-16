#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HttpClient = System.Net.Http.HttpClient;

namespace DeadlockPlayground.Tools;

public sealed class ChangelogEntry
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("markdown")]
    public string Markdown { get; set; } = "";
}

public sealed class ChangelogPayload
{
    [JsonPropertyName("latestVersion")]
    public string LatestVersion { get; set; } = "1.0.1";

    [JsonPropertyName("publishedAt")]
    public string PublishedAt { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "What's New in Deadlock Playground";

    [JsonPropertyName("markdown")]
    public string Markdown { get; set; } = "";

    [JsonPropertyName("history")]
    public List<ChangelogEntry> History { get; set; } = new();

    [JsonPropertyName("links")]
    public Dictionary<string, string> Links { get; set; } = new();

    [JsonIgnore]
    public bool IsFromRemote { get; set; }

    [JsonIgnore]
    public bool IsFromCache { get; set; }
}

/// <summary>
/// Service responsible for fetching dynamic changelogs and news from the Deadlock Playground website API,
/// with offline disk caching and bundled fallback support.
/// </summary>
public static class ChangelogFeedService
{
    public const string DefaultApiUrl = "https://deadlock-playground.pages.dev/api/changelog.json";
    public const string CacheFileName = "changelog_cache.json";
    public const string UserAgentHeader = "DeadlockPlayground-Client";

    private static readonly HttpClient _httpClient = CreateHttpClient();
    private static ChangelogPayload? _cachedInMemory;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgentHeader);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static string GetCacheFilePath()
    {
        string cacheDir = ProjectSettings.GlobalizePath("user://cache");
        if (!Directory.Exists(cacheDir))
        {
            try { Directory.CreateDirectory(cacheDir); } catch { }
        }
        return Path.Combine(cacheDir, CacheFileName);
    }

    /// <summary>
    /// Fetches the latest changelog payload from the remote API, falling back to local disk cache or bundled text.
    /// </summary>
    public static async Task<ChangelogPayload> GetChangelogAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && _cachedInMemory != null)
        {
            return _cachedInMemory;
        }

        // 1. Try fetching from remote API
        try
        {
            using var response = await _httpClient.GetAsync(DefaultApiUrl, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var payload = JsonSerializer.Deserialize<ChangelogPayload>(json, options);
                if (payload != null && (!string.IsNullOrEmpty(payload.Markdown) || payload.History.Count > 0))
                {
                    payload.IsFromRemote = true;
                    _cachedInMemory = payload;

                    // Persist to disk cache
                    try
                    {
                        File.WriteAllText(GetCacheFilePath(), json);
                    }
                    catch (Exception ex)
                    {
                        GD.Print($"[ChangelogFeedService] Failed writing cache: {ex.Message}");
                    }

                    return payload;
                }
            }
        }
        catch (Exception ex)
        {
            GD.Print($"[ChangelogFeedService] Remote fetch failed ({ex.Message}), checking local cache...");
        }

        // 2. Try loading from local disk cache
        try
        {
            string cachePath = GetCacheFilePath();
            if (File.Exists(cachePath))
            {
                string json = File.ReadAllText(cachePath);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var cached = JsonSerializer.Deserialize<ChangelogPayload>(json, options);
                if (cached != null)
                {
                    cached.IsFromCache = true;
                    _cachedInMemory = cached;
                    return cached;
                }
            }
        }
        catch (Exception ex)
        {
            GD.Print($"[ChangelogFeedService] Cache load failed: {ex.Message}");
        }

        // 3. Bundled fallback
        var fallback = new ChangelogPayload
        {
            LatestVersion = ChangelogModalUI.CurrentVersion,
            Title = "What's New in Deadlock Playground",
            Markdown = ChangelogModalUI.InitialChangelogMarkdown,
            IsFromRemote = false,
            IsFromCache = false,
            Links = new Dictionary<string, string>
            {
                ["website"] = "https://deadlock-playground.pages.dev",
                ["github"] = "https://github.com/juliancz-a/deadlock-playground",
                ["gamebanana"] = "https://gamebanana.com/tools/24316"
            }
        };

        _cachedInMemory = fallback;
        return fallback;
    }
}

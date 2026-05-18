using System.Collections.Concurrent;
using System.Text.Json;
using XboxApi.Models;

namespace XboxApi.Services;

public class ConfigService : IConfigService
{
    private readonly string _configFilePath;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ConfigService> _logger;
    private readonly ConcurrentDictionary<string, string?> _coverArtCache = new();
    private AppConfig _config = new();
    private string? _igdbAccessToken;
    private DateTime _igdbTokenExpiry = DateTime.MinValue;

    public ConfigService(HttpClient httpClient, ILogger<ConfigService> logger)
    {
        _configFilePath = Path.Combine(Directory.GetCurrentDirectory(), "config.json");
        _httpClient = httpClient;
        _logger = logger;
        LoadConfigAsync().GetAwaiter().GetResult();
    }

    public Task<AppConfig> GetConfigAsync()
    {
        return Task.FromResult(_config);
    }

    public async Task UpdateConfigAsync(ConfigUpdateRequest request)
    {
        _config.IgdbClientId = request.IgdbClientId?.Trim();
        _config.IgdbClientSecret = request.IgdbClientSecret?.Trim();
        _coverArtCache.Clear();
        _igdbAccessToken = null;
        await SaveConfigAsync();
        _logger.LogInformation("Updated IGDB credentials, cover art {Status}", _config.EnableCoverArt ? "enabled" : "disabled");
    }

    public async Task<string?> GetGameCoverArtAsync(string gameName)
    {
        if (!_config.EnableCoverArt)
            return null;

        var cleanedName = CleanGameName(gameName);

        if (_coverArtCache.TryGetValue(cleanedName, out var cachedUrl))
            return cachedUrl;

        try
        {
            var token = await GetIgdbTokenAsync();
            if (token == null)
            {
                _logger.LogWarning("Could not get IGDB access token");
                return null;
            }

            // Search for the game
            var searchRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games");
            searchRequest.Headers.Add("Client-ID", _config.IgdbClientId);
            searchRequest.Headers.Add("Authorization", $"Bearer {token}");
            searchRequest.Content = new StringContent(
                $"search \"{cleanedName}\"; fields name,cover; limit 1;",
                System.Text.Encoding.UTF8,
                "text/plain"
            );

            var searchResponse = await _httpClient.SendAsync(searchRequest);
            if (!searchResponse.IsSuccessStatusCode)
            {
                _coverArtCache[cleanedName] = null;
                return null;
            }

            var searchJson = await searchResponse.Content.ReadAsStringAsync();
            var games = JsonSerializer.Deserialize<List<IgdbGame>>(searchJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var coverId = games?.FirstOrDefault()?.Cover;
            if (coverId == null)
            {
                _coverArtCache[cleanedName] = null;
                return null;
            }

            // Get cover art URL
            var coverRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/covers");
            coverRequest.Headers.Add("Client-ID", _config.IgdbClientId);
            coverRequest.Headers.Add("Authorization", $"Bearer {token}");
            coverRequest.Content = new StringContent(
                $"fields url; where id = {coverId};",
                System.Text.Encoding.UTF8,
                "text/plain"
            );

            var coverResponse = await _httpClient.SendAsync(coverRequest);
            if (!coverResponse.IsSuccessStatusCode)
            {
                _coverArtCache[cleanedName] = null;
                return null;
            }

            var coverJson = await coverResponse.Content.ReadAsStringAsync();
            var covers = JsonSerializer.Deserialize<List<IgdbCover>>(coverJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var coverUrl = covers?.FirstOrDefault()?.Url;
            if (!string.IsNullOrEmpty(coverUrl))
            {
                // IGDB geeft //images.igdb.com/... terug, zet https: ervoor en gebruik hd formaat
                coverUrl = "https:" + coverUrl.Replace("t_thumb", "t_cover_big");
            }

            _coverArtCache[cleanedName] = coverUrl;

            if (!string.IsNullOrEmpty(coverUrl))
                _logger.LogDebug("Found IGDB cover art for {GameName}: {CoverUrl}", gameName, coverUrl);
            else
                _logger.LogDebug("No IGDB cover art found for {GameName}", gameName);

            return coverUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get IGDB cover art for game: {GameName}", gameName);
            _coverArtCache[cleanedName] = null;
            return null;
        }
    }

    private async Task<string?> GetIgdbTokenAsync()
    {
        if (_igdbAccessToken != null && DateTime.UtcNow < _igdbTokenExpiry)
            return _igdbAccessToken;

        try
        {
            var tokenRequest = new HttpRequestMessage(HttpMethod.Post,
                $"https://id.twitch.tv/oauth2/token?client_id={_config.IgdbClientId}&client_secret={_config.IgdbClientSecret}&grant_type=client_credentials");

            var tokenResponse = await _httpClient.SendAsync(tokenRequest);
            if (!tokenResponse.IsSuccessStatusCode)
                return null;

            var tokenJson = await tokenResponse.Content.ReadAsStringAsync();
            var tokenData = JsonSerializer.Deserialize<IgdbTokenResponse>(tokenJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            _igdbAccessToken = tokenData?.AccessToken;
            _igdbTokenExpiry = DateTime.UtcNow.AddSeconds((tokenData?.ExpiresIn ?? 3600) - 60);

            return _igdbAccessToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get IGDB token");
            return null;
        }
    }

    private async Task LoadConfigAsync()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                var json = await File.ReadAllTextAsync(_configFilePath);
                var config = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (config != null)
                {
                    _config = config;
                    _logger.LogInformation("Loaded config, cover art {Status}", _config.EnableCoverArt ? "enabled" : "disabled");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load config");
        }
    }

    private async Task SaveConfigAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_configFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save config");
        }
    }

    private static string CleanGameName(string gameName)
    {
        return gameName
            .Replace("™", "")
            .Replace("®", "")
            .Replace("©", "")
            .Trim();
    }

    private class IgdbGame
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public int? Cover { get; set; }
    }

    private class IgdbCover
    {
        public int Id { get; set; }
        public string? Url { get; set; }
    }

    private class IgdbTokenResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }
        public int ExpiresIn { get; set; }
    }
}

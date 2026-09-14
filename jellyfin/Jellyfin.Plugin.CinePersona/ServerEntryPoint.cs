using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CinePersona.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CinePersona;

public sealed class ServerEntryPoint : IHostedService
{
    private const double CompletionThreshold = 0.80d;
    private static readonly TimeSpan ReverseSyncInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan ReverseSyncInitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReverseSyncPollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient HttpClient = new();

    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ServerEntryPoint> _logger;
    private readonly SemaphoreSlim _reverseSyncLock = new(1, 1);
    private Timer? _reverseSyncTimer;
    private IUserDataManager? _userDataManager;

    public ServerEntryPoint(
        ISessionManager sessionManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILogger<ServerEntryPoint> logger,
        IServiceProvider serviceProvider)
    {
        _sessionManager = sessionManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.PluginPaths is { } applicationPaths)
        {
            Plugin.InjectWebScript(applicationPaths, _logger);
        }
        _userDataManager = _serviceProvider.GetService<IUserDataManager>();
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        if (_userDataManager is not null)
        {
            _userDataManager.UserDataSaved += OnUserDataSaved;
        }
        _reverseSyncTimer = new Timer(
            _ => _ = RunReverseSyncSafelyAsync(),
            null,
            ReverseSyncInitialDelay,
            ReverseSyncPollInterval);
        _logger.LogInformation(
            "CinePersona service started; user data events: {UserDataEvents}",
            _userDataManager is not null ? "enabled" : "unavailable");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _reverseSyncTimer?.Dispose();
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        if (_userDataManager is not null)
        {
            _userDataManager.UserDataSaved -= OnUserDataSaved;
        }
        return Task.CompletedTask;
    }

    private async Task RunReverseSyncSafelyAsync()
    {
        if (!await _reverseSyncLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await PullAndApplyReverseSyncAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "CinePersona reverse sync failed");
        }
        finally
        {
            _reverseSyncLock.Release();
        }
    }

    private async Task PullAndApplyReverseSyncAsync()
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null || !configuration.ReverseSyncEnabled || _userDataManager is null)
        {
            return;
        }

        var apiKey = configuration.ApiKey.Trim();
        var syncUserId = configuration.SyncUserId.Trim();
        if (apiKey.Length == 0 || syncUserId.Length == 0)
        {
            return;
        }

        if (!Guid.TryParse(syncUserId, out var userId))
        {
            _logger.LogWarning("CinePersona reverse sync user id is invalid");
            return;
        }

        var isInitialSync = !configuration.InitialSyncCompleted
            || !string.Equals(configuration.LastSyncUserId, syncUserId, StringComparison.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        if (!isInitialSync
            && configuration.LastSyncAt.HasValue
            && now - configuration.LastSyncAt.Value.ToUniversalTime() < ReverseSyncInterval)
        {
            return;
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            _logger.LogWarning("CinePersona reverse sync user was not found: {UserId}", syncUserId);
            return;
        }

        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive = true,
            EnableTotalRecordCount = false
        }).Items;
        var movieIndex = BuildMovieIndex(movies);
        DateTime? since = null;
        if (!isInitialSync && configuration.LastSyncAt.HasValue)
        {
            since = configuration.LastSyncAt.Value.ToUniversalTime().AddMinutes(-5);
        }

        var offset = 0;
        var imported = 0;
        var matched = 0;
        for (var pageNumber = 0; pageNumber < 100; pageNumber++)
        {
            var page = await FetchSyncPageAsync(configuration, apiKey, since, offset).ConfigureAwait(false);
            var activities = page.Activities ?? Array.Empty<SyncActivity>();
            foreach (var activity in activities)
            {
                imported++;
                if (ApplySyncActivity(user, activity, movieIndex))
                {
                    matched++;
                }
            }

            if (page.Paging is null || !page.Paging.HasMore || activities.Length == 0)
            {
                break;
            }

            offset += activities.Length;
            if (pageNumber == 99)
            {
                throw new InvalidOperationException("CinePersona reverse sync exceeded 100 pages");
            }
        }

        configuration.InitialSyncCompleted = true;
        configuration.LastSyncAt = now;
        configuration.LastSyncUserId = syncUserId;
        Plugin.Instance?.SaveConfiguration();
        _logger.LogInformation(
            "CinePersona reverse sync completed: {Imported} records, {Matched} local movies",
            imported,
            matched);
    }

    private async Task<SyncPullResponse> FetchSyncPageAsync(
        PluginConfiguration configuration,
        string apiKey,
        DateTime? since,
        int offset)
    {
        var baseUrl = string.IsNullOrWhiteSpace(configuration.ServerUrl)
            ? "https://cinepersona.com"
            : configuration.ServerUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serverUri)
            || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("CinePersona server URL is invalid");
        }

        var query = $"?limit=200&offset={offset.ToString(CultureInfo.InvariantCulture)}";
        if (since.HasValue)
        {
            var sinceText = since.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            query += "&since=" + Uri.EscapeDataString(sinceText);
        }

        var endpoint = new Uri(serverUri, "/open/v1/sync" + query);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Add("X-API-Key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var timeout = new CancellationTokenSource(RequestTimeout);
        using var response = await HttpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"CinePersona reverse sync returned {(int)response.StatusCode}: {responseBody}");
        }

        var result = JsonSerializer.Deserialize<SyncPullResponse>(
            responseBody,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (result is null || !result.Success)
        {
            throw new InvalidOperationException("CinePersona reverse sync returned invalid data");
        }

        return result;
    }

    private bool ApplySyncActivity(User user, SyncActivity activity, IDictionary<string, Movie> movieIndex)
    {
        if (activity?.Movie is null)
        {
            return false;
        }

        var movie = FindMovie(activity.Movie, movieIndex);
        if (movie is null)
        {
            return false;
        }

        var userData = _userDataManager!.GetUserData(user, movie);
        var changed = false;
        if (!userData.Played)
        {
            userData.Played = true;
            userData.PlayCount = Math.Max(userData.PlayCount, 1);
            userData.PlaybackPositionTicks = movie.RunTimeTicks ?? userData.PlaybackPositionTicks;
            if (!userData.LastPlayedDate.HasValue && TryParseDate(activity.WatchedAt, out var watchedAt))
            {
                userData.LastPlayedDate = watchedAt;
            }
            changed = true;
        }

        if ((!userData.Rating.HasValue || userData.Rating.Value <= 0)
            && activity.Rating.HasValue
            && activity.Rating.Value > 0)
        {
            userData.Rating = activity.Rating.Value;
            changed = true;
        }

        if (changed)
        {
            _userDataManager.SaveUserData(user, movie, userData, UserDataSaveReason.Import, CancellationToken.None);
        }

        return true;
    }

    private static IDictionary<string, Movie> BuildMovieIndex(IEnumerable<BaseItem> items)
    {
        var index = new Dictionary<string, Movie>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item is not Movie movie)
            {
                continue;
            }

            AddMovieKey(index, "imdb:", movie.ProviderIds, "Imdb", movie);
            AddMovieKey(index, "tmdb:", movie.ProviderIds, "Tmdb", movie);
        }

        return index;
    }

    private static void AddMovieKey(
        IDictionary<string, Movie> index,
        string prefix,
        IDictionary<string, string> providerIds,
        string provider,
        Movie movie)
    {
        if (providerIds is not null
            && providerIds.TryGetValue(provider, out var value)
            && !string.IsNullOrWhiteSpace(value))
        {
            var key = prefix + value.Trim();
            if (!index.ContainsKey(key))
            {
                index[key] = movie;
            }
        }
    }

    private static Movie? FindMovie(SyncMovie movie, IDictionary<string, Movie> movieIndex)
    {
        if (!string.IsNullOrWhiteSpace(movie.ImdbId)
            && movieIndex.TryGetValue("imdb:" + movie.ImdbId.Trim(), out var byImdb))
        {
            return byImdb;
        }

        if (movie.TmdbId.HasValue
            && movieIndex.TryGetValue("tmdb:" + movie.TmdbId.Value.ToString(CultureInfo.InvariantCulture), out var byTmdb))
        {
            return byTmdb;
        }

        return null;
    }

    private static bool TryParseDate(string? value, out DateTime parsed)
    {
        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed);
    }

    private sealed class SyncPullResponse
    {
        public bool Success { get; set; }

        public SyncPaging? Paging { get; set; }

        public SyncActivity[]? Activities { get; set; }
    }

    private sealed class SyncPaging
    {
        public bool HasMore { get; set; }
    }

    private sealed class SyncActivity
    {
        public string? Status { get; set; }

        public double? Rating { get; set; }

        public string? WatchedAt { get; set; }

        public SyncMovie? Movie { get; set; }
    }

    private sealed class SyncMovie
    {
        public int? TmdbId { get; set; }

        public string? ImdbId { get; set; }
    }

    private async void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            await SyncPlaybackAsync(e).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "CinePersona sync failed with an unhandled exception");
        }
    }

    private async void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e?.Item is not Movie movie || e.UserData is null)
        {
            return;
        }

        if (e.SaveReason == UserDataSaveReason.TogglePlayed && e.UserData.Played)
        {
            try
            {
                var runtimeTicks = movie.RunTimeTicks ?? 0;
                var positionTicks = runtimeTicks > 0
                    ? runtimeTicks
                    : e.UserData.PlaybackPositionTicks;
                await SyncMovieAsync(movie, positionTicks, "手动标记看过").ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "CinePersona manual watched sync failed");
            }
        }
        else if (e.SaveReason == UserDataSaveReason.UpdateUserRating && e.UserData.Rating.HasValue)
        {
            try
            {
                await SyncMovieAsync(
                    movie,
                    movie.RunTimeTicks ?? 0,
                    "Jellyfin 星级评价",
                    e.UserData.Rating).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "CinePersona rating sync failed");
            }
        }
    }

    private async Task SyncPlaybackAsync(PlaybackStopEventArgs e)
    {
        if (e.Item is not Movie movie)
        {
            return;
        }

        await SyncMovieAsync(movie, e.PlaybackPositionTicks ?? 0, "播放停止").ConfigureAwait(false);
    }

    private async Task SyncMovieAsync(Movie movie, long positionTicks, string trigger, double? rating = null)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null)
        {
            return;
        }

        var apiKey = configuration.ApiKey.Trim();
        if (apiKey.Length == 0)
        {
            _logger.LogDebug("CinePersona API key is not configured");
            return;
        }

        var runtimeTicks = movie.RunTimeTicks ?? 0;
        if (!rating.HasValue && !HasReachedCompletion(runtimeTicks, positionTicks))
        {
            var progress = runtimeTicks > 0 ? (double)positionTicks / runtimeTicks : 0d;
            _logger.LogDebug("Skipping {Movie}: completion was {Progress:P0}", movie.Name, progress);
            return;
        }

        var baseUrl = string.IsNullOrWhiteSpace(configuration.ServerUrl)
            ? "https://cinepersona.com"
            : configuration.ServerUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serverUri)
            || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
        {
            _logger.LogWarning("CinePersona server URL is invalid");
            return;
        }

        movie.ProviderIds.TryGetValue("Imdb", out var imdbId);
        movie.ProviderIds.TryGetValue("Tmdb", out var tmdbId);
        var payload = new
        {
            Event = rating.HasValue ? "userData.rating" : "playback.stop",
            Item = new
            {
                Type = "Movie",
                Name = movie.Name,
                ProductionYear = movie.ProductionYear ?? 0,
                RunTimeTicks = runtimeTicks,
                ProviderIds = new
                {
                    Imdb = imdbId ?? string.Empty,
                    Tmdb = tmdbId ?? string.Empty
                }
            },
            PlaybackInfo = new
            {
                PositionTicks = positionTicks
            },
            UserData = new
            {
                Played = true,
                Rating = rating
            }
        };

        var endpoint = new Uri(serverUri, "/v1/webhook/jellyfin");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("X-API-Key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var timeout = new CancellationTokenSource(RequestTimeout);
        using var response = await HttpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if ((int)response.StatusCode == 200
            && responseBody.IndexOf("\"status\":\"ignored\"", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _logger.LogInformation(
                "CinePersona sync ignored for {Movie}, trigger: {Trigger}",
                movie.Name,
                trigger);
            return;
        }

        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation(
                "CinePersona sync succeeded for {Movie}, trigger: {Trigger}",
                movie.Name,
                trigger);
            return;
        }

        _logger.LogWarning(
            "CinePersona sync failed for {Movie}: {StatusCode} {ResponseBody}",
            movie.Name,
            (int)response.StatusCode,
            responseBody);
    }

    internal static bool HasReachedCompletion(long runtimeTicks, long positionTicks)
    {
        return runtimeTicks > 0
            && positionTicks > 0
            && (double)positionTicks / runtimeTicks >= CompletionThreshold;
    }
}

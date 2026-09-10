using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.CinePersona.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CinePersona;

public sealed class ServerEntryPoint : IHostedService
{
    private const double CompletionThreshold = 0.80d;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient HttpClient = new();

    private readonly ISessionManager _sessionManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<ServerEntryPoint> _logger;

    public ServerEntryPoint(
        ISessionManager sessionManager,
        IUserDataManager userDataManager,
        ILogger<ServerEntryPoint> logger)
    {
        _sessionManager = sessionManager;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.ApplicationPaths is { } applicationPaths)
        {
            Plugin.InjectWebScript(applicationPaths, _logger);
        }
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _userDataManager.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
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

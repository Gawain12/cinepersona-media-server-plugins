using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.CinePersona.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CinePersona;

public sealed class ServerEntryPoint : IHostedService
{
    private const double CompletionThreshold = 0.80d;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient HttpClient = new();

    private readonly ISessionManager _sessionManager;
    private readonly ILogger<ServerEntryPoint> _logger;

    public ServerEntryPoint(
        ISessionManager sessionManager,
        ILogger<ServerEntryPoint> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
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

    private async Task SyncPlaybackAsync(PlaybackStopEventArgs e)
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

        if (e.Item is not Movie movie)
        {
            return;
        }

        var runtimeTicks = movie.RunTimeTicks ?? 0;
        var positionTicks = e.PlaybackPositionTicks ?? 0;
        if (!HasReachedCompletion(runtimeTicks, positionTicks))
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
            Event = "playback.stop",
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
        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("CinePersona sync succeeded for {Movie}", movie.Name);
            return;
        }

        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
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

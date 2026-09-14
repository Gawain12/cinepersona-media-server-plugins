using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CinePersona.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.CinePersona;

public sealed class CinePersonaReviewRequest
{
    public string? ItemId { get; set; }

    public string? Name { get; set; }

    public int ProductionYear { get; set; }

    public string? ImdbId { get; set; }

    public string? TmdbId { get; set; }

    public double Rating { get; set; }

    public string? ReviewText { get; set; }

    public bool HasSpoiler { get; set; }
}

public sealed class CinePersonaActivityResponse
{
    public bool Success { get; set; }

    public string? MovieId { get; set; }

    public CinePersonaActivity? Activity { get; set; }
}

public sealed class CinePersonaActivity
{
    public string? Status { get; set; }

    public double? Rating { get; set; }

    public string? ReviewText { get; set; }

    public bool HasSpoiler { get; set; }

    public string? WatchedAt { get; set; }

    public string? RatedAt { get; set; }
}

[ApiController]
[Authorize]
[Route("CinePersona")]
public sealed class CinePersonaController : ControllerBase
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient HttpClient = new();

    [HttpGet("Activity")]
    public async Task<IActionResult> Activity(
        [FromQuery] string? imdbId,
        [FromQuery] string? tmdbId,
        CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var apiKey = (configuration.ApiKey ?? string.Empty).Trim();
        if (apiKey.Length == 0)
        {
            return StatusCode(503, new { error = "CinePersona API key is not configured" });
        }

        imdbId = (imdbId ?? string.Empty).Trim();
        tmdbId = (tmdbId ?? string.Empty).Trim();
        if (imdbId.Length == 0 && tmdbId.Length == 0)
        {
            return BadRequest(new { error = "IMDb or TMDB ID is required" });
        }

        var baseUrl = string.IsNullOrWhiteSpace(configuration.ServerUrl)
            ? "https://cinepersona.com"
            : configuration.ServerUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serverUri)
            || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
        {
            return StatusCode(503, new { error = "CinePersona server URL is invalid" });
        }

        var query = "?imdbId=" + Uri.EscapeDataString(imdbId) + "&tmdbId=" + Uri.EscapeDataString(tmdbId);
        var endpoint = new Uri(serverUri, "/open/v1/movies/lookup/rating" + query);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, endpoint);
        httpRequest.Headers.Add("X-API-Key", apiKey);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await HttpClient.SendAsync(httpRequest, timeout.Token).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return StatusCode((int)response.StatusCode, new { error = "CinePersona activity lookup failed", detail = responseBody });
        }

        var result = JsonSerializer.Deserialize<CinePersonaActivityResponse>(
            responseBody,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return Ok(result ?? new CinePersonaActivityResponse { Success = false });
    }

    [HttpPost("Review")]
    public async Task<IActionResult> Review(
        [FromBody] CinePersonaReviewRequest request,
        CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var apiKey = configuration.ApiKey.Trim();
        if (apiKey.Length == 0)
        {
            return StatusCode(503, new { error = "CinePersona API key is not configured" });
        }

        if (request is null || request.Rating < 0.5d || request.Rating > 10d)
        {
            return BadRequest(new { error = "rating must be between 0.5 and 10" });
        }

        var reviewText = (request.ReviewText ?? string.Empty).Trim();
        if (reviewText.Length > 4000)
        {
            return BadRequest(new { error = "review must be 4000 characters or less" });
        }

        var baseUrl = string.IsNullOrWhiteSpace(configuration.ServerUrl)
            ? "https://cinepersona.com"
            : configuration.ServerUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serverUri)
            || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
        {
            return StatusCode(503, new { error = "CinePersona server URL is invalid" });
        }

        var payload = new
        {
            Event = "userData.rating",
            Item = new
            {
                Type = "Movie",
                Name = (request.Name ?? string.Empty).Trim(),
                ProductionYear = request.ProductionYear,
                RunTimeTicks = 0L,
                ProviderIds = new
                {
                    Imdb = (request.ImdbId ?? string.Empty).Trim(),
                    Tmdb = (request.TmdbId ?? string.Empty).Trim()
                }
            },
            PlaybackInfo = new
            {
                PositionTicks = 0L
            },
            UserData = new
            {
                Played = true,
                Rating = request.Rating,
                Comment = reviewText,
                HasSpoiler = request.HasSpoiler && reviewText.Length > 0
            }
        };

        var endpoint = new Uri(serverUri, "/v1/webhook/jellyfin");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.Add("X-API-Key", apiKey);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await HttpClient.SendAsync(httpRequest, timeout.Token).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return StatusCode((int)response.StatusCode, new { error = "CinePersona sync failed", detail = responseBody });
        }

        return Ok(new { success = true });
    }
}

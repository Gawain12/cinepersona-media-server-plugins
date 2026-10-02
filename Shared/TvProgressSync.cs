#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CinePersona.MediaSync
{
    // Shared by both server plugins: identity and observation ordering must agree.
    public sealed class TvProgressSync
    {
        private readonly HttpClient client;
        private readonly Func<object, string> serialize;
        private readonly Func<DateTime> clock;
        private readonly object gate = new object();
        private readonly Dictionary<string, ObservationState> states = new Dictionary<string, ObservationState>();

        public TvProgressSync(HttpClient client, Func<object, string> serialize, Func<DateTime>? clock = null)
        {
            this.client = client;
            this.serialize = serialize;
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        public async Task<bool> SendAsync(string serverUrl, string apiKey, string userId,
            string seriesTvdbId, string episodeTvdbId, int? season, int? episode,
            long positionTicks, long durationTicks, bool paused, bool force,
            string platform, string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(userId)
                || !int.TryParse(seriesTvdbId, NumberStyles.None, CultureInfo.InvariantCulture, out var showId) || showId <= 0
                || positionTicks < 0 || durationTicks <= 0 || durationTicks > TimeSpan.FromDays(10).Ticks)
                return false;

            var hasEpisodeId = int.TryParse(episodeTvdbId, NumberStyles.None, CultureInfo.InvariantCulture, out var episodeId) && episodeId > 0;
            if (!hasEpisodeId && (!season.HasValue || season < 0 || !episode.HasValue || episode <= 0))
                return false;
            var baseUrl = string.IsNullOrWhiteSpace(serverUrl) ? "https://cinepersona.com" : serverUrl.Trim().TrimEnd('/');
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serverUri)
                || serverUri == null || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
                return false;

            deviceId = deviceId ?? string.Empty;
            if (deviceId.Length > 128) deviceId = deviceId.Substring(0, 128);
            var now = clock().ToUniversalTime(); // Capture before HTTP awaits; retries keep this timestamp.
            var key = serverUri + "|" + userId + "|" + showId + "|"
                + (hasEpisodeId ? "id:" + episodeId : "num:" + season + ":" + episode) + "|" + deviceId;
            lock (gate)
            {
                // Bound state even on servers with many users/episodes; remove old observations.
                var expired = new List<string>();
                foreach (var pair in states)
                    if (now - pair.Value.At > TimeSpan.FromHours(2)) expired.Add(pair.Key);
                foreach (var old in expired) states.Remove(old);
                if (!force && states.TryGetValue(key, out var previous)
                    && previous.Paused == paused && now - previous.At < TimeSpan.FromSeconds(30))
                    return false;
                states[key] = new ObservationState { At = now, Paused = paused };
            }

            var position = Math.Min(positionTicks, durationTicks) / (double)TimeSpan.TicksPerSecond;
            var duration = durationTicks / (double)TimeSpan.TicksPerSecond;
            var percentage = 100d * position / duration;
            var payload = new Dictionary<string, object>
            {
                ["progress"] = percentage,
                ["positionSeconds"] = position,
                ["durationSeconds"] = duration,
                ["completed"] = percentage >= 80d,
                ["observedAt"] = now.ToString("O", CultureInfo.InvariantCulture),
                ["platform"] = platform,
                ["deviceId"] = deviceId
            };
            // Exact TVDB episode ID avoids DVD/absolute numbering mismatches.
            if (hasEpisodeId) payload["tvdbEpisodeId"] = episodeId;
            else { payload["season"] = season.GetValueOrDefault(); payload["episode"] = episode.GetValueOrDefault(); }
            var json = serialize(payload);
            var endpoint = new Uri(serverUri, "/open/v1/me/tv/tvdb:" + showId.ToString(CultureInfo.InvariantCulture) + "/progress");
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Put, endpoint))
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                    {
                        request.Headers.Add("X-API-Key", apiKey.Trim());
                        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                        using (var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false))
                        {
                            if (response.IsSuccessStatusCode) return true;
                            // Missing/ambiguous catalog entries or auth errors must never title-match or replay.
                            if (attempt != 0 || ((int)response.StatusCode != 429 && (int)response.StatusCode < 500))
                                throw new InvalidOperationException("CinePersona TV progress returned HTTP " + (int)response.StatusCode);
                        }
                    }
                }
                catch (HttpRequestException) when (attempt == 0) { }
                catch (OperationCanceledException) when (attempt == 0) { }
                await Task.Delay(1000).ConfigureAwait(false);
            }
            return false;
        }

        private sealed class ObservationState
        {
            public DateTime At;
            public bool Paused;
        }
    }
}

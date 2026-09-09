using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.CinePersona.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.Plugin.CinePersona
{
    public class ServerEntryPoint : IServerEntryPoint
    {
        private const double CompletionThreshold = 0.80d;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
        private static readonly HttpClient HttpClient = new HttpClient();

        private readonly ISessionManager _sessionManager;
        private readonly ILogger _logger;
        private readonly IJsonSerializer _jsonSerializer;

        public ServerEntryPoint(
            ISessionManager sessionManager,
            ILogManager logManager,
            IJsonSerializer jsonSerializer)
        {
            _sessionManager = sessionManager;
            _logger = logManager.GetLogger("CinePersona");
            _jsonSerializer = jsonSerializer;
        }

        public Task RunAsync()
        {
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            return Task.CompletedTask;
        }

        private async void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            try
            {
                await SyncPlaybackAsync(e).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("CinePersona 同步触发异常", ex);
            }
        }

        private async Task SyncPlaybackAsync(PlaybackStopEventArgs e)
        {
            var plugin = Plugin.Instance;
            if (plugin == null)
            {
                return;
            }

            var configuration = plugin.Configuration ?? new PluginConfiguration();
            var apiKey = (configuration.ApiKey ?? string.Empty).Trim();
            if (apiKey.Length == 0)
            {
                _logger.Debug("CinePersona API Key 未配置，跳过同步");
                return;
            }

            if (!(e.Item is Movie movie))
            {
                return;
            }

            var runtimeTicks = movie.RunTimeTicks ?? 0;
            var positionTicks = e.PlaybackPositionTicks ?? 0;
            if (!HasReachedCompletion(runtimeTicks, positionTicks))
            {
                var progress = runtimeTicks > 0
                    ? (double)positionTicks / runtimeTicks
                    : 0d;
                _logger.Info($"观影比例未达到 80% ({progress:P0})，跳过 CinePersona 打卡");
                return;
            }

            var baseUrl = string.IsNullOrWhiteSpace(configuration.ServerUrl)
                ? "https://cinepersona.com"
                : configuration.ServerUrl.Trim().TrimEnd('/');
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serverUri)
                || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
            {
                _logger.Warn("CinePersona 服务器地址无效，跳过同步");
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

            var endpoint = new Uri(serverUri, "/api/v1/webhook/emby");
            var json = _jsonSerializer.SerializeToString(payload);

            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                request.Headers.Add("X-API-Key", apiKey);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using (var timeout = new CancellationTokenSource(RequestTimeout))
                using (var response = await HttpClient.SendAsync(request, timeout.Token).ConfigureAwait(false))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        _logger.Info($"CinePersona 同步成功: {movie.Name} ({(int)response.StatusCode})");
                        return;
                    }

                    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    _logger.Warn($"CinePersona 同步失败: {(int)response.StatusCode} - {responseBody}");
                }
            }
        }

        internal static bool HasReachedCompletion(long runtimeTicks, long positionTicks)
        {
            return runtimeTicks > 0
                && positionTicks > 0
                && (double)positionTicks / runtimeTicks >= CompletionThreshold;
        }

        public void Dispose()
        {
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        }
    }
}

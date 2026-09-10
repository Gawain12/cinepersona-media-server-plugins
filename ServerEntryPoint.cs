using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.CinePersona.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
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
        private readonly IUserDataManager _userDataManager;
        private readonly ILogger _logger;
        private readonly IJsonSerializer _jsonSerializer;

        public ServerEntryPoint(
            ISessionManager sessionManager,
            IUserDataManager userDataManager,
            ILogManager logManager,
            IJsonSerializer jsonSerializer)
        {
            _sessionManager = sessionManager;
            _userDataManager = userDataManager;
            _logger = logManager.GetLogger("CinePersona");
            _jsonSerializer = jsonSerializer;
        }

        public void Run()
        {
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            _userDataManager.UserDataSaved += OnUserDataSaved;
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

        private async void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            // Emby uses TogglePlayed when the user clicks "Mark as played".
            // PlaybackStopped does not fire for that action, so treat it as a
            // completed movie at 100% and send the same server-side event.
            if (e == null || e.UserData == null || !(e.Item is Movie movie))
            {
                return;
            }

            try
            {
                var runtimeTicks = movie.RunTimeTicks ?? 0;
                if (e.SaveReason == UserDataSaveReason.TogglePlayed && e.UserData.Played)
                {
                    var positionTicks = runtimeTicks > 0
                        ? runtimeTicks
                        : e.UserData.PlaybackPositionTicks;
                    await SyncMovieAsync(movie, positionTicks, "手动标记看过", null).ConfigureAwait(false);
                    return;
                }

                if (e.SaveReason == UserDataSaveReason.UpdateUserRating && e.UserData.Rating.HasValue)
                {
                    await SyncMovieAsync(movie, runtimeTicks, "Emby 星级评价", e.UserData.Rating).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("CinePersona 手动标记同步触发异常", ex);
            }
        }

        private async Task SyncPlaybackAsync(PlaybackStopEventArgs e)
        {
            if (!(e.Item is Movie movie))
            {
                return;
            }

            await SyncMovieAsync(movie, e.PlaybackPositionTicks ?? 0, "播放完成", null).ConfigureAwait(false);
        }

        private async Task SyncMovieAsync(Movie movie, long positionTicks, string trigger, double? rating)
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

            var runtimeTicks = movie.RunTimeTicks ?? 0;
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

            var endpoint = new Uri(serverUri, "/v1/webhook/emby");
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
                        _logger.Info($"CinePersona 同步成功: {movie.Name}，触发方式：{trigger} ({(int)response.StatusCode})");
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
            _userDataManager.UserDataSaved -= OnUserDataSaved;
        }
    }
}

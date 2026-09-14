using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.CinePersona.Configuration;
using MediaBrowser.Controller.Entities;
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
        private static readonly TimeSpan ReverseSyncInterval = TimeSpan.FromHours(6);
        private static readonly TimeSpan ReverseSyncInitialDelay = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ReverseSyncPollInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
        private static readonly HttpClient HttpClient = new HttpClient();

        private readonly ISessionManager _sessionManager;
        private readonly IUserDataManager _userDataManager;
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly SemaphoreSlim _reverseSyncLock = new SemaphoreSlim(1, 1);
        private Timer _reverseSyncTimer;

        public ServerEntryPoint(
            ISessionManager sessionManager,
            IUserDataManager userDataManager,
            IUserManager userManager,
            ILibraryManager libraryManager,
            ILogManager logManager,
            IJsonSerializer jsonSerializer)
        {
            _sessionManager = sessionManager;
            _userDataManager = userDataManager;
            _userManager = userManager;
            _libraryManager = libraryManager;
            _logger = logManager.GetLogger("CinePersona");
            _jsonSerializer = jsonSerializer;
        }

        public void Run()
        {
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            _userDataManager.UserDataSaved += OnUserDataSaved;
            _reverseSyncTimer = new Timer(
                _ => _ = RunReverseSyncSafelyAsync(),
                null,
                ReverseSyncInitialDelay,
                ReverseSyncPollInterval);
        }

        private async void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            try
            {
                await SyncPlaybackAsync(e, e?.Session?.UserId.ToString()).ConfigureAwait(false);
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
                    await SyncMovieAsync(movie, positionTicks, "手动标记看过", null, e.User?.Id.ToString()).ConfigureAwait(false);
                    return;
                }

                if (e.SaveReason == UserDataSaveReason.UpdateUserRating && e.UserData.Rating.HasValue)
                {
                    await SyncMovieAsync(movie, runtimeTicks, "Emby 星级评价", e.UserData.Rating, e.User?.Id.ToString()).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("CinePersona 手动标记同步触发异常", ex);
            }
        }

        private async Task SyncPlaybackAsync(PlaybackStopEventArgs e, string userId)
        {
            if (!(e.Item is Movie movie))
            {
                return;
            }

            await SyncMovieAsync(movie, e.PlaybackPositionTicks ?? 0, "播放完成", null, userId).ConfigureAwait(false);
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
            catch (Exception ex)
            {
                _logger.ErrorException("CinePersona 反向同步失败", ex);
            }
            finally
            {
                _reverseSyncLock.Release();
            }
        }

        private async Task PullAndApplyReverseSyncAsync()
        {
            var plugin = Plugin.Instance;
            var configuration = plugin?.Configuration;
            if (configuration == null || !configuration.ReverseSyncEnabled)
            {
                return;
            }

            var movies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie" },
                Recursive = true,
                EnableTotalRecordCount = false
            });
            var movieIndex = BuildMovieIndex(movies);
            var changed = false;
            foreach (var profile in configuration.GetConfiguredUserProfiles())
            {
                var apiKey = (profile.ApiKey ?? string.Empty).Trim();
                if (!profile.Enabled || apiKey.Length == 0 || !Guid.TryParse(profile.UserId, out var userId))
                {
                    continue;
                }

                var isInitialSync = !profile.InitialSyncCompleted;
                var now = DateTime.UtcNow;
                if (!isInitialSync
                    && profile.LastSyncAt.HasValue
                    && now - profile.LastSyncAt.Value.ToUniversalTime() < ReverseSyncInterval)
                {
                    continue;
                }

                var user = _userManager.GetUserById(userId);
                if (user == null)
                {
                    _logger.Warn($"CinePersona 反向同步找不到 Emby 用户: {profile.UserId}");
                    continue;
                }

                DateTime? since = null;
                if (!isInitialSync && profile.LastSyncAt.HasValue)
                {
                    since = profile.LastSyncAt.Value.ToUniversalTime().AddMinutes(-5);
                }

                var offset = 0;
                var imported = 0;
                var matched = 0;
                for (var pageNumber = 0; pageNumber < 100; pageNumber++)
                {
                    var page = await FetchSyncPageAsync(configuration, apiKey, since, offset).ConfigureAwait(false);
                    var activities = page.Activities ?? new SyncActivity[0];
                    foreach (var activity in activities)
                    {
                        imported++;
                        if (ApplySyncActivity(user, activity, movieIndex))
                        {
                            matched++;
                        }
                    }

                    if (page.Paging == null || !page.Paging.HasMore || activities.Length == 0)
                    {
                        break;
                    }

                    offset += activities.Length;
                    if (pageNumber == 99)
                    {
                        throw new InvalidOperationException("CinePersona 反向同步超过 100 页，已中止");
                    }
                }

                profile.InitialSyncCompleted = true;
                profile.LastSyncAt = now;
                changed = true;
                _logger.Info($"CinePersona 反向同步完成: 用户 {profile.UserId}，{imported} 条记录，匹配本地电影 {matched} 条");
            }

            if (changed)
            {
                plugin.SaveConfiguration();
            }
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
                throw new InvalidOperationException("CinePersona 服务器地址无效");
            }

            var query = "?limit=200&offset=" + offset.ToString(CultureInfo.InvariantCulture);
            if (since.HasValue)
            {
                var sinceText = since.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                query += "&since=" + Uri.EscapeDataString(sinceText);
            }

            var endpoint = new Uri(serverUri, "/open/v1/sync" + query);
            using (var request = new HttpRequestMessage(HttpMethod.Get, endpoint))
            {
                request.Headers.Add("X-API-Key", apiKey);
                using (var timeout = new CancellationTokenSource(RequestTimeout))
                using (var response = await HttpClient.SendAsync(request, timeout.Token).ConfigureAwait(false))
                {
                    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException($"CinePersona 反向同步接口返回 {(int)response.StatusCode}: {responseBody}");
                    }

                    var result = _jsonSerializer.DeserializeFromString<SyncPullResponse>(responseBody);
                    if (result == null || !result.Success)
                    {
                        throw new InvalidOperationException("CinePersona 反向同步接口返回无效数据");
                    }

                    return result;
                }
            }
        }

        private bool ApplySyncActivity(User user, SyncActivity activity, IDictionary<string, Movie> movieIndex)
        {
            if (activity == null || activity.Movie == null)
            {
                return false;
            }

            var movie = FindMovie(activity.Movie, movieIndex);
            if (movie == null)
            {
                return false;
            }

            var userData = _userDataManager.GetUserData(user, movie);
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
                if (!(item is Movie movie))
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
            if (providerIds != null && providerIds.TryGetValue(provider, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                var key = prefix + value.Trim();
                if (!index.ContainsKey(key))
                {
                    index[key] = movie;
                }
            }
        }

        private static Movie FindMovie(SyncMovie movie, IDictionary<string, Movie> movieIndex)
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

        private static bool TryParseDate(string value, out DateTime parsed)
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

            public SyncPaging Paging { get; set; } = new SyncPaging();

            public SyncActivity[] Activities { get; set; }
        }

        private sealed class SyncPaging
        {
            public bool HasMore { get; set; }
        }

        private sealed class SyncActivity
        {
            public string Status { get; set; }

            public double? Rating { get; set; }

            public string WatchedAt { get; set; }

            public SyncMovie Movie { get; set; }
        }

        private sealed class SyncMovie
        {
            public int? TmdbId { get; set; }

            public string ImdbId { get; set; }
        }

        private async Task SyncMovieAsync(Movie movie, long positionTicks, string trigger, double? rating, string userId)
        {
            var plugin = Plugin.Instance;
            if (plugin == null)
            {
                return;
            }

            var configuration = plugin.Configuration ?? new PluginConfiguration();
            var profile = configuration.FindUserProfile(userId);
            if (profile == null && string.Equals(configuration.SyncUserId, userId, StringComparison.OrdinalIgnoreCase))
            {
                profile = new UserSyncProfile
                {
                    UserId = userId,
                    ApiKey = configuration.ApiKey,
                    Enabled = true
                };
            }

            var apiKey = profile?.Enabled == true ? (profile.ApiKey ?? string.Empty).Trim() : string.Empty;
            if (apiKey.Length == 0)
            {
                _logger.Debug($"CinePersona 用户 {userId} 未配置 API Key，跳过同步");
                return;
            }

            var runtimeTicks = movie.RunTimeTicks ?? 0;
            if (!rating.HasValue && !HasReachedCompletion(runtimeTicks, positionTicks))
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
                    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if ((int)response.StatusCode == 200
                        && responseBody.IndexOf("\"status\":\"ignored\"", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _logger.Info($"CinePersona 已忽略同步: {movie.Name}，触发方式：{trigger}");
                        return;
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        _logger.Info($"CinePersona 同步成功: {movie.Name}，触发方式：{trigger} ({(int)response.StatusCode})");
                        return;
                    }

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
            _reverseSyncTimer?.Dispose();
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            _userDataManager.UserDataSaved -= OnUserDataSaved;
        }
    }
}

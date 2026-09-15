#if CINEPERSONA_GEEK
using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.CinePersona.Configuration;
using MediaBrowser.Common;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Serialization;

namespace Emby.Plugin.CinePersona
{
    [Route("/CinePersona/Review", "POST")]
    [Authenticated]
    public class CinePersonaReviewRequest
    {
        public string ItemId { get; set; }

        public string Name { get; set; }

        public int ProductionYear { get; set; }

        public string ImdbId { get; set; }

        public string TmdbId { get; set; }

        public double Rating { get; set; }

        public string ReviewText { get; set; }

        public bool HasSpoiler { get; set; }
    }

    [Route("/CinePersona/Activity", "GET")]
    [Authenticated]
    public class CinePersonaActivityRequest
    {
        public string ImdbId { get; set; }

        public string TmdbId { get; set; }
    }

    [Route("/CinePersona/Settings", "GET")]
    [Authenticated]
    public class CinePersonaSettingsRequest
    {
    }

    [Route("/CinePersona/Settings", "POST")]
    [Authenticated]
    public class CinePersonaSettingsSaveRequest
    {
        public string ApiKey { get; set; }

        public bool Enabled { get; set; } = true;
    }

    [Route("/CinePersona/DeviceCode", "POST")]
    [Authenticated]
    public class CinePersonaDeviceCodeRequest
    {
    }

    [Route("/CinePersona/DevicePoll", "POST")]
    [Authenticated]
    public class CinePersonaDevicePollRequest
    {
        public string DeviceCode { get; set; }
    }

    public class DevicePollResult
    {
        public bool Success { get; set; }

        public string Status { get; set; }

        public string ApiKey { get; set; }

        public string UserId { get; set; }

        public string UserName { get; set; }
    }

    public class CinePersonaActivityResponse
    {
        public bool Success { get; set; }

        public string MovieId { get; set; }

        public CinePersonaActivity Activity { get; set; }
    }

    public class CinePersonaActivity
    {
        public string Status { get; set; }

        public double? Rating { get; set; }

        public string ReviewText { get; set; }

        public bool HasSpoiler { get; set; }

        public string WatchedAt { get; set; }

        public string RatedAt { get; set; }
    }

    public class CinePersonaWebService : IService, IRequiresRequest
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
        private static readonly HttpClient HttpClient = new HttpClient();

        private readonly IJsonSerializer _jsonSerializer;
        private readonly IAuthorizationContext _authorizationContext;

        public IRequest Request { get; set; }

        public CinePersonaWebService(IJsonSerializer jsonSerializer, IAuthorizationContext authorizationContext)
        {
            _jsonSerializer = jsonSerializer;
            _authorizationContext = authorizationContext;
        }

        public object Get(CinePersonaSettingsRequest request)
        {
            var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var userId = GetCurrentUserId();
            var profile = configuration.FindUserProfile(userId);
            if (profile == null && string.Equals(configuration.SyncUserId, userId, StringComparison.OrdinalIgnoreCase))
            {
                profile = new UserSyncProfile
                {
                    UserId = userId,
                    ApiKey = (configuration.ApiKey ?? string.Empty).Trim(),
                    Enabled = true
                };
            }

            return new
            {
                Configured = profile != null && !string.IsNullOrWhiteSpace(profile.ApiKey),
                Enabled = profile == null || profile.Enabled,
                UserId = userId
            };
        }

        public object Post(CinePersonaSettingsSaveRequest request)
        {
            var plugin = Plugin.Instance;
            var configuration = plugin?.Configuration ?? new PluginConfiguration();
            var userId = GetCurrentUserId();
            if (userId.Length == 0)
            {
                throw new InvalidOperationException("无法识别当前 Emby 用户");
            }

            var profile = configuration.GetOrCreateUserProfile(userId);
            if (profile == null)
            {
                throw new InvalidOperationException("无法创建 CinePersona 用户配置");
            }

            var enteredApiKey = (request?.ApiKey ?? string.Empty).Trim();
            if (enteredApiKey.Length > 0)
            {
                profile.ApiKey = enteredApiKey;
            }

            if (string.IsNullOrWhiteSpace(profile.ApiKey))
            {
                throw new InvalidOperationException("请填写 CinePersona API Key");
            }

            profile.Enabled = request?.Enabled ?? true;
            plugin?.SaveConfiguration();
            return new
            {
                Success = true,
                Configured = true,
                Enabled = profile.Enabled,
                UserId = userId
            };
        }

        public async Task<object> Post(CinePersonaDeviceCodeRequest request)
        {
            var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var baseUrl = string.IsNullOrWhiteSpace(configuration.ServerUrl)
                ? "https://cinepersona.com"
                : configuration.ServerUrl.Trim().TrimEnd('/');
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serverUri)
                || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("CinePersona 服务器地址无效");
            }

            var endpoint = new Uri(serverUri, "/open/v1/device/code");
            var payload = new { clientName = "Emby" };
            using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                httpRequest.Content = new StringContent(
                    _jsonSerializer.SerializeToString(payload),
                    Encoding.UTF8,
                    "application/json");

                using (var timeout = new CancellationTokenSource(RequestTimeout))
                using (var response = await HttpClient.SendAsync(httpRequest, timeout.Token).ConfigureAwait(false))
                {
                    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(string.Format("CinePersona 申请设备码失败: {0}", responseBody));
                    }
                    return _jsonSerializer.DeserializeFromString<object>(responseBody);
                }
            }
        }

        public async Task<object> Post(CinePersonaDevicePollRequest request)
        {
            var plugin = Plugin.Instance;
            var configuration = plugin?.Configuration ?? new PluginConfiguration();
            var userId = GetCurrentUserId();
            if (string.IsNullOrEmpty(userId))
            {
                throw new InvalidOperationException("无法识别当前 Emby 用户");
            }

            var deviceCode = (request?.DeviceCode ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(deviceCode))
            {
                throw new ArgumentException("deviceCode 不能为空");
            }

            var baseUrl = string.IsNullOrWhiteSpace(configuration.ServerUrl)
                ? "https://cinepersona.com"
                : configuration.ServerUrl.Trim().TrimEnd('/');
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serverUri)
                || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("CinePersona 服务器地址无效");
            }

            var endpoint = new Uri(serverUri, "/open/v1/device/poll");
            var payload = new { deviceCode = deviceCode };
            using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                httpRequest.Content = new StringContent(
                    _jsonSerializer.SerializeToString(payload),
                    Encoding.UTF8,
                    "application/json");

                using (var timeout = new CancellationTokenSource(RequestTimeout))
                using (var response = await HttpClient.SendAsync(httpRequest, timeout.Token).ConfigureAwait(false))
                {
                    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(string.Format("CinePersona 轮询设备码失败: {0}", responseBody));
                    }

                    try
                    {
                        var pollResult = _jsonSerializer.DeserializeFromString<DevicePollResult>(responseBody);
                        if (pollResult != null && pollResult.Success && string.Equals(pollResult.Status, "approved", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pollResult.ApiKey))
                        {
                            var profile = configuration.GetOrCreateUserProfile(userId);
                            if (profile != null)
                            {
                                profile.ApiKey = pollResult.ApiKey.Trim();
                                profile.Enabled = true;
                                plugin?.SaveConfiguration();
                            }
                        }
                    }
                    catch
                    {
                        // Ignore parse exceptions on non-approved states
                    }

                    return _jsonSerializer.DeserializeFromString<object>(responseBody);
                }
            }
        }

        private string GetCurrentUserId()
        {
            var authorization = _authorizationContext?.GetAuthorizationInfo(Request);
            return authorization?.UserId.ToString() ?? string.Empty;
        }

        public async Task<object> Post(CinePersonaReviewRequest request)
        {
            var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var apiKey = GetCurrentApiKey(configuration);
            if (apiKey.Length == 0)
            {
                throw new InvalidOperationException("当前 Emby 用户尚未配置 CinePersona API Key");
            }

            if (request == null || request.Rating < 0.5d || request.Rating > 10d)
            {
                throw new ArgumentException("Rating 必须在 0.5 到 10 之间");
            }

            var reviewText = (request.ReviewText ?? string.Empty).Trim();
            if (reviewText.Length > 4000)
            {
                throw new ArgumentException("ReviewText 不能超过 4000 个字符");
            }

            var baseUrl = string.IsNullOrWhiteSpace(configuration.ServerUrl)
                ? "https://cinepersona.com"
                : configuration.ServerUrl.Trim().TrimEnd('/');
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serverUri)
                || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("CinePersona 服务器地址无效");
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

            var endpoint = new Uri(serverUri, "/v1/webhook/emby");
            using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                httpRequest.Headers.Add("X-API-Key", apiKey);
                httpRequest.Content = new StringContent(
                    _jsonSerializer.SerializeToString(payload),
                    Encoding.UTF8,
                    "application/json");

                using (var timeout = new CancellationTokenSource(RequestTimeout))
                using (var response = await HttpClient.SendAsync(httpRequest, timeout.Token).ConfigureAwait(false))
                {
                    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            string.Format("CinePersona 返回 HTTP {0}: {1}", (int)response.StatusCode, responseBody));
                    }
                }
            }

            return new
            {
                Success = true
            };
        }

        public async Task<object> Get(CinePersonaActivityRequest request)
        {
            var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var apiKey = GetCurrentApiKey(configuration);
            if (apiKey.Length == 0)
            {
                throw new InvalidOperationException("当前 Emby 用户尚未配置 CinePersona API Key");
            }

            var imdbId = (request?.ImdbId ?? string.Empty).Trim();
            var tmdbId = (request?.TmdbId ?? string.Empty).Trim();
            if (imdbId.Length == 0 && tmdbId.Length == 0)
            {
                throw new ArgumentException("必须提供 IMDb 或 TMDB ID");
            }

            var baseUrl = string.IsNullOrWhiteSpace(configuration.ServerUrl)
                ? "https://cinepersona.com"
                : configuration.ServerUrl.Trim().TrimEnd('/');
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serverUri)
                || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("CinePersona 服务器地址无效");
            }

            var query = string.Format(
                "?imdbId={0}&tmdbId={1}",
                Uri.EscapeDataString(imdbId),
                Uri.EscapeDataString(tmdbId));
            var endpoint = new Uri(serverUri, "/open/v1/movies/lookup/rating" + query);
            using (var httpRequest = new HttpRequestMessage(HttpMethod.Get, endpoint))
            {
                httpRequest.Headers.Add("X-API-Key", apiKey);
                using (var timeout = new CancellationTokenSource(RequestTimeout))
                using (var response = await HttpClient.SendAsync(httpRequest, timeout.Token).ConfigureAwait(false))
                {
                    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            string.Format("CinePersona 返回 HTTP {0}: {1}", (int)response.StatusCode, responseBody));
                    }

                    return _jsonSerializer.DeserializeFromString<CinePersonaActivityResponse>(responseBody);
                }
            }
        }

        private string GetCurrentApiKey(PluginConfiguration configuration)
        {
            var userId = GetCurrentUserId();
            var profile = configuration.FindUserProfile(userId);
            if (profile != null)
            {
                return profile.Enabled ? (profile.ApiKey ?? string.Empty).Trim() : string.Empty;
            }

            // Keep installations that explicitly configured the old single-user
            // mode working, without ever using its key for another user.
            if (string.Equals(configuration.SyncUserId, userId, StringComparison.OrdinalIgnoreCase))
            {
                return (configuration.ApiKey ?? string.Empty).Trim();
            }

            return string.Empty;
        }
    }
}
#endif

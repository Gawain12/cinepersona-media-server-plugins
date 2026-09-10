using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugin.CinePersona.Configuration;
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
    }

    public class CinePersonaWebService : IService
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
        private static readonly HttpClient HttpClient = new HttpClient();

        private readonly IJsonSerializer _jsonSerializer;

        public CinePersonaWebService(IJsonSerializer jsonSerializer)
        {
            _jsonSerializer = jsonSerializer;
        }

        public async Task<object> Post(CinePersonaReviewRequest request)
        {
            var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var apiKey = (configuration.ApiKey ?? string.Empty).Trim();
            if (apiKey.Length == 0)
            {
                throw new InvalidOperationException("CinePersona API Key 未配置");
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
                    Comment = reviewText
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
    }
}

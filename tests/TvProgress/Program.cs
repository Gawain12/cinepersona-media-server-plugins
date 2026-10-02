using System.Net;
using System.Text.Json;
using CinePersona.MediaSync;

var now = DateTime.UtcNow;
var http = new CaptureHandler();
var sync = new TvProgressSync(new HttpClient(http), value => JsonSerializer.Serialize(value), () => now);
Task<bool> Send(long seconds = 600, bool paused = false, bool force = false, string show = "296762", string ep = "123", string user = "user-a", string device = "device-a", long duration = 2400, int? season = 1, int? episode = 3) =>
    sync.SendAsync("https://test.gawyn.de", "test-only-key", user, show, ep, season, episode,
        TimeSpan.FromSeconds(seconds).Ticks, TimeSpan.FromSeconds(duration).Ticks, paused, force, "Jellyfin", device);
void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
Check(await Send(), "partial episode saved without movie 80% gate");
var first = JsonDocument.Parse(http.Bodies[0]).RootElement;
Check(first.GetProperty("progress").GetDouble() == 25 && first.GetProperty("positionSeconds").GetDouble() == 600 && first.GetProperty("durationSeconds").GetDouble() == 2400, "ticks convert to actual seconds and percentage");
Check(!first.GetProperty("completed").GetBoolean(), "partial episode is not completed");
Check(first.GetProperty("tvdbEpisodeId").GetInt32() == 123 && !first.TryGetProperty("season", out _), "exact TVDB ID does not assert conflicting numbering");
Check(http.Paths[0] == "/open/v1/me/tv/tvdb:296762/progress", "TV route cannot hit movie webhook");
Check(!await Send(610), "repeated progress throttled for 30 seconds");
Check(await Send(620, paused: true), "pause flushes immediately");
Check(!await Send(620, paused: true), "repeated pause events throttled");
Check(await Send(620, paused: true, force: true), "stop bypasses throttle");
now = now.AddSeconds(31);
Check(await Send(500), "rewind remains lower rather than maximum percentage");
Check(await Send(user: "user-b"), "user throttle isolation");
Check(await Send(device: "device-b"), "device throttle isolation");
Check(!await Send(show: ""), "missing series identity skips");
Check(!await Send(ep: "", season: null), "missing episode identity skips");
Check(!await Send(duration: 0), "unknown runtime does not invent a percentage");
Check(await Send(ep: "", episode: 4), "season and episode fallback works");
var numbered = JsonDocument.Parse(http.Bodies[^1]).RootElement;
Check(numbered.GetProperty("season").GetInt32() == 1 && numbered.GetProperty("episode").GetInt32() == 4, "fallback fields match API contract");
Check(await Send(2000, force: true), "completed progress accepted");
Check(JsonDocument.Parse(http.Bodies[^1]).RootElement.GetProperty("completed").GetBoolean(), "80 percent episode completion");
Check(await Send(2500, force: true), "player overrun clamped");
Check(JsonDocument.Parse(http.Bodies[^1]).RootElement.GetProperty("progress").GetDouble() == 100, "clamped percentage consistent with seconds");
http.Statuses.Enqueue(HttpStatusCode.ServiceUnavailable);
http.Statuses.Enqueue(HttpStatusCode.OK);
var before = http.Bodies.Count;
Check(await Send(900, force: true), "transient failure retried");
Check(http.Bodies[before] == http.Bodies[before + 1], "retry retains original observation and idempotency");
foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Conflict, HttpStatusCode.Unauthorized })
{
    http.Statuses.Enqueue(status);
    before = http.Bodies.Count;
    try { await Send(force: true); throw new Exception("unexpected success"); }
    catch (InvalidOperationException) { }
    Check(http.Bodies.Count == before + 1, "no retry or title fallback for " + status);
}
Console.WriteLine("All TV progress contract checks passed.");

sealed class CaptureHandler : HttpMessageHandler
{
    public List<string> Bodies { get; } = new();
    public List<string> Paths { get; } = new();
    public Queue<HttpStatusCode> Statuses { get; } = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.Method != HttpMethod.Put || request.Headers.GetValues("X-API-Key").Single() != "test-only-key") throw new Exception("Bad request auth or method");
        Paths.Add(request.RequestUri!.AbsolutePath);
        Bodies.Add(await request.Content!.ReadAsStringAsync(token));
        return new HttpResponseMessage(Statuses.Count > 0 ? Statuses.Dequeue() : HttpStatusCode.OK);
    }
}

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using WebViewDriver.Host;
using Xunit;

namespace WebViewDriver.Tests;

public sealed class HostServerTests : IDisposable
{
    private const string Token = "unit-test-token";

    private readonly HostServer _server;
    private readonly HttpClient _http;

    public HostServerTests()
    {
        _server = new HostServer(Ports.GetFree(), Token);
        _server.Start();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/") };
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? body = null, string? token = Token)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Add("X-WebViewDriver-Token", token);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    [Fact]
    public async Task Rejects_missing_token()
    {
        var response = await _http.SendAsync(Request(HttpMethod.Get, "status", token: null));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_wrong_token()
    {
        var response = await _http.SendAsync(Request(HttpMethod.Get, "status", token: "wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Status_reports_protocol_and_targets()
    {
        _server.AttachTarget("main", new FakeScriptHost());
        _server.AttachTarget("settings", new FakeScreenshotScriptHost());

        var response = await _http.SendAsync(Request(HttpMethod.Get, "status"));
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(1, (int?)json["protocolVersion"]);
        var targets = json["targets"]!.AsArray();
        Assert.Equal(2, targets.Count);
        Assert.Equal("main", (string?)targets[0]!["name"]);
        Assert.False((bool?)targets[0]!["screenshots"]);
        Assert.True((bool?)targets[1]!["screenshots"]);
    }

    [Fact]
    public async Task Eval_routes_to_named_target()
    {
        var host = new FakeScriptHost();
        host.CannedResponses.Enqueue("\"hello\"");
        _server.AttachTarget("main", host);

        var response = await _http.SendAsync(Request(HttpMethod.Post, "eval",
            """{"target":"main","script":"'hello'"}"""));
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.True((bool?)json["ok"]);
        Assert.Equal("\"hello\"", (string?)json["result"]);
        Assert.Equal("'hello'", Assert.Single(host.ReceivedScripts));
    }

    [Fact]
    public async Task Eval_unknown_target_reports_error_code()
    {
        var response = await _http.SendAsync(Request(HttpMethod.Post, "eval",
            """{"target":"nope","script":"1"}"""));
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.False((bool?)json["ok"]);
        Assert.Equal("unknown target", (string?)json["error"]!["code"]);
    }

    [Fact]
    public async Task Eval_timeout_is_reported()
    {
        var host = new FakeScriptHost { Delay = TimeSpan.FromSeconds(5), Responder = _ => "late" };
        _server.AttachTarget("main", host);

        var response = await _http.SendAsync(Request(HttpMethod.Post, "eval",
            """{"target":"main","script":"1","timeoutMs":100}"""));
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.False((bool?)json["ok"]);
        Assert.Equal("eval timeout", (string?)json["error"]!["code"]);
    }

    [Fact]
    public async Task Oversized_request_is_rejected()
    {
        var request = Request(HttpMethod.Post, "eval");
        request.Content = new ByteArrayContent(new byte[5 * 1024 * 1024]);
        try
        {
            var response = await _http.SendAsync(request);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        catch (HttpRequestException)
        {
            // Also acceptable: the server refuses the body and closes the
            // connection before the client finishes streaming 5 MB.
        }
    }

    [Fact]
    public async Task Evals_are_single_flight_per_target()
    {
        var active = 0;
        var maxActive = 0;
        var host = new FakeScriptHost
        {
            Delay = TimeSpan.FromMilliseconds(100),
            Responder = _ => "x",
        };
        // Wrap the responder to count concurrency around the delay window.
        host.Responder = _ =>
        {
            var now = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maxActive, now);
            Thread.Sleep(80);
            Interlocked.Decrement(ref active);
            return "\"x\"";
        };
        host.Delay = TimeSpan.Zero;
        _server.AttachTarget("main", host);

        var tasks = Enumerable.Range(0, 4).Select(_ => _http.SendAsync(Request(HttpMethod.Post, "eval",
            """{"target":"main","script":"1"}"""))).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task Status_reports_app_declared_readiness()
    {
        _server.AttachTarget("main", new FakeScriptHost());
        _server.SetTargetReady("main", false);

        var json = JsonNode.Parse(await (await _http.SendAsync(Request(HttpMethod.Get, "status"))).Content.ReadAsStringAsync())!;
        Assert.False((bool?)json["targets"]![0]!["ready"]);

        _server.SetTargetReady("main", true);
        json = JsonNode.Parse(await (await _http.SendAsync(Request(HttpMethod.Get, "status"))).Content.ReadAsStringAsync())!;
        Assert.True((bool?)json["targets"]![0]!["ready"]);
    }

    [Fact]
    public async Task Screenshot_unsupported_without_provider()
    {
        _server.AttachTarget("main", new FakeScriptHost());
        var response = await _http.SendAsync(Request(HttpMethod.Post, "screenshot", """{"target":"main"}"""));
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.False((bool?)json["ok"]);
        Assert.Equal("unsupported", (string?)json["error"]!["code"]);
    }

    [Fact]
    public async Task Screenshot_returns_png_from_provider()
    {
        var provider = new FakeScreenshotScriptHost();
        _server.AttachTarget("main", provider);
        var response = await _http.SendAsync(Request(HttpMethod.Post, "screenshot", """{"target":"main"}"""));
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.True((bool?)json["ok"]);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, Convert.FromBase64String((string)json["dataBase64"]!));
        Assert.Null(provider.LastRequestedRegion);
    }

    [Fact]
    public async Task Screenshot_forwards_the_requested_region()
    {
        var provider = new FakeScreenshotScriptHost();
        _server.AttachTarget("main", provider);
        var response = await _http.SendAsync(Request(HttpMethod.Post, "screenshot",
            """{"target":"main","rect":{"x":10.5,"y":20,"width":120,"height":32}}"""));
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.True((bool?)json["ok"]);
        Assert.Equal(new WebViewDriver.Host.ScreenshotRegion(10.5, 20, 120, 32), provider.LastRequestedRegion);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }
}

internal static class InterlockedExtensions
{
    public static void Max(ref int location, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref location)))
        {
            if (Interlocked.CompareExchange(ref location, value, current) == current)
            {
                return;
            }
        }
    }
}

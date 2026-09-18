using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenQA.Selenium;

namespace WebViewDriver.Selenium;

/// <summary>Thrown for transport-level failures with a clear error taxonomy (FR10).</summary>
public sealed class WebViewDriverConnectionException : WebDriverException
{
    public WebViewDriverConnectionException(string message) : base(message) { }
    public WebViewDriverConnectionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>HTTP transport to the in-app WebViewDriver.Host endpoint.</summary>
internal sealed class WireClient : IDisposable
{
    public const int SupportedProtocolVersion = 1;

    private readonly HttpClient _http;
    private readonly int _port;
    private readonly string? _wireCaptureFile;
    private readonly object _captureLock = new();

    public WireClient(int port, string token, string? wireCaptureFile = null)
    {
        _port = port;
        _wireCaptureFile = wireCaptureFile;
        _http = new HttpClient
        {
            BaseAddress = new Uri(FormattableString.Invariant($"http://127.0.0.1:{port}/")),
            Timeout = Timeout.InfiniteTimeSpan, // per-request timeouts via CancellationToken
        };
        _http.DefaultRequestHeaders.Add("X-WebViewDriver-Token", token);
    }

    public async Task<JsonNode> GetStatusAsync(CancellationToken cancellationToken)
    {
        var json = await SendAsync(HttpMethod.Get, "status", requestBody: null, cancellationToken).ConfigureAwait(false);
        var protocolVersion = (int?)json["protocolVersion"];
        if (protocolVersion != SupportedProtocolVersion)
        {
            throw new WebViewDriverConnectionException(
                $"Protocol mismatch: the app hosts WebViewDriver protocol v{protocolVersion?.ToString() ?? "?"} but this client speaks v{SupportedProtocolVersion}. " +
                "Align the WebViewDriver.Host and WebViewDriver.Selenium package versions.");
        }

        return json;
    }

    /// <summary>Sends a script to the app and returns the raw string the webview evaluated to.</summary>
    public async Task<string> EvalAsync(string target, string script, int timeoutMs, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["target"] = target,
            ["script"] = script,
            ["timeoutMs"] = timeoutMs,
        }.ToJsonString();

        var json = await SendAsync(HttpMethod.Post, "eval", body, cancellationToken).ConfigureAwait(false);
        if ((bool?)json["ok"] != true)
        {
            var code = (string?)json["error"]?["code"] ?? "unknown";
            var message = (string?)json["error"]?["message"] ?? "";
            throw new WebViewDriverConnectionException(
                $"The app's eval endpoint reported '{code}': {message} " +
                (code == "unknown target"
                    ? "The endpoint is up but this webview is not attached — check the name passed to WebViewDriverHost.Attach."
                    : ""));
        }

        return (string?)json["result"] ?? "null";
    }

    public async Task<byte[]> TakeScreenshotAsync(string target, JsonObject? rect, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["target"] = target, ["rect"] = rect }.ToJsonString();
        var json = await SendAsync(HttpMethod.Post, "screenshot", body, cancellationToken).ConfigureAwait(false);
        if ((bool?)json["ok"] != true)
        {
            var code = (string?)json["error"]?["code"] ?? "unknown";
            var message = (string?)json["error"]?["message"] ?? "";
            throw new WebDriverException($"Screenshot failed ({code}): {message}");
        }

        return Convert.FromBase64String((string?)json["dataBase64"] ?? "");
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, string? requestBody, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (requestBody is not null)
        {
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new WebViewDriverConnectionException(
                $"Could not reach the WebViewDriver endpoint on 127.0.0.1:{_port}. " +
                "Is the app running with WEBVIEWDRIVER_PORT/WEBVIEWDRIVER_TOKEN set, and did it call WebViewDriverHost.TryStartFromEnvironment()?", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new WebViewDriverConnectionException(
                    "The WebViewDriver endpoint rejected the session token. " +
                    "Pass the same WEBVIEWDRIVER_TOKEN value to the app process and to WebViewDriverClient.Connect.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new WebViewDriverConnectionException(
                    $"The WebViewDriver endpoint answered {(int)response.StatusCode} for /{path}.");
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Capture(path, requestBody, payload);
            try
            {
                return JsonNode.Parse(payload) ?? throw new JsonException("null payload");
            }
            catch (JsonException ex)
            {
                throw new WebViewDriverConnectionException(
                    $"The WebViewDriver endpoint returned malformed JSON for /{path}. This usually means a host/client protocol mismatch.", ex);
            }
        }
    }

    private void Capture(string path, string? requestBody, string responseBody)
    {
        if (_wireCaptureFile is null)
        {
            return;
        }

        var line = new JsonObject
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["path"] = path,
            ["request"] = requestBody,
            ["response"] = responseBody,
        }.ToJsonString();

        lock (_captureLock)
        {
            File.AppendAllText(_wireCaptureFile, line + Environment.NewLine);
        }
    }

    public void Dispose() => _http.Dispose();
}

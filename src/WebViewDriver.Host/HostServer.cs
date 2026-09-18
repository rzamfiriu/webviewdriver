using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WebViewDriver.Host;

/// <summary>
/// The loopback HTTP listener. Deliberately protocol-dumb: it only knows how to
/// authenticate requests, look up a named target, and forward a script to it.
/// All WebDriver semantics live in the test-side client.
/// </summary>
internal sealed class HostServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly byte[] _tokenUtf8;
    private readonly ConcurrentDictionary<string, Registration> _targets = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();

    private sealed record Registration(IScriptHost ScriptHost)
    {
        public SemaphoreSlim EvalLock { get; } = new(1, 1);

        /// <summary>App-declared readiness (FR9); null = never declared. Benign races: read on /status only.</summary>
        public bool? Ready { get; set; }
    }

    public int Port { get; }

    public HostServer(int port, string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            throw new ArgumentException("A non-empty token is required.", nameof(token));
        }

        Port = port;
        _tokenUtf8 = Encoding.UTF8.GetBytes(token);
        // SR1: loopback only, not configurable.
        _listener.Prefixes.Add(FormattableString.Invariant($"http://127.0.0.1:{port}/"));
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public void AttachTarget(string name, IScriptHost scriptHost)
        => _targets[name] = new Registration(scriptHost);

    public void DetachTarget(string name)
        => _targets.TryRemove(name, out _);

    public void SetTargetReady(string name, bool ready)
    {
        if (_targets.TryGetValue(name, out var registration))
        {
            registration.Ready = ready;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_shutdown.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }

            _ = Task.Run(() => HandleRequestSafeAsync(context));
        }
    }

    private async Task HandleRequestSafeAsync(HttpListenerContext context)
    {
        try
        {
            await HandleRequestAsync(context).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
                // Client went away; nothing to do.
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        if (!IsAuthorized(request))
        {
            response.StatusCode = 401;
            response.Close();
            return;
        }

        if (request.ContentLength64 > WireProtocol.MaxRequestBytes)
        {
            response.StatusCode = 413;
            response.Close();
            return;
        }

        var path = request.Url?.AbsolutePath ?? "/";
        switch (request.HttpMethod, path)
        {
            case ("GET", "/status"):
                await WriteJsonAsync(response, 200, BuildStatus(), WireJsonContext.Default.StatusResponse).ConfigureAwait(false);
                break;

            case ("POST", "/eval"):
                await HandleEvalAsync(request, response).ConfigureAwait(false);
                break;

            case ("POST", "/screenshot"):
                await HandleScreenshotAsync(request, response).ConfigureAwait(false);
                break;

            default:
                response.StatusCode = 404;
                response.Close();
                break;
        }
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        // SR3: random per-session token on every request, constant-time compared.
        var supplied = request.Headers["X-WebViewDriver-Token"];
        if (string.IsNullOrEmpty(supplied))
        {
            return false;
        }

        var suppliedUtf8 = Encoding.UTF8.GetBytes(supplied);
        return CryptographicOperations.FixedTimeEquals(suppliedUtf8, _tokenUtf8);
    }

    private StatusResponse BuildStatus() => new()
    {
        ProtocolVersion = WireProtocol.Version,
        HostVersion = typeof(HostServer).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        Targets = _targets
            .Select(kvp => new TargetStatus
            {
                Name = kvp.Key,
                Screenshots = kvp.Value.ScriptHost is IScreenshotProvider,
                Ready = kvp.Value.Ready,
            })
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList(),
    };

    private async Task HandleEvalAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        var eval = await ReadJsonBodyAsync(request, WireJsonContext.Default.EvalRequest).ConfigureAwait(false);
        if (eval?.Target is null || eval.Script is null)
        {
            await WriteJsonAsync(response, 400,
                Error(WireErrorCodes.BadRequest, "Body must contain 'target' and 'script'."),
                WireJsonContext.Default.EvalResponse).ConfigureAwait(false);
            return;
        }

        if (!_targets.TryGetValue(eval.Target, out var registration))
        {
            await WriteJsonAsync(response, 200,
                Error(WireErrorCodes.UnknownTarget, $"No webview is attached under the name '{eval.Target}'."),
                WireJsonContext.Default.EvalResponse).ConfigureAwait(false);
            return;
        }

        var timeoutMs = Math.Clamp(eval.TimeoutMs ?? WireProtocol.DefaultEvalTimeoutMs, 1, WireProtocol.DefaultEvalTimeoutMs * 10);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(timeoutMs);

        // SR5: single-flight eval queue per target.
        try
        {
            await registration.EvalLock.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteJsonAsync(response, 200,
                Error(WireErrorCodes.EvalTimeout, $"Timed out after {timeoutMs} ms waiting for a previous eval to finish."),
                WireJsonContext.Default.EvalResponse).ConfigureAwait(false);
            return;
        }

        try
        {
            var result = await registration.ScriptHost
                .EvaluateJavaScriptAsync(eval.Script, timeout.Token)
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
            await WriteJsonAsync(response, 200, new EvalResponse { Ok = true, Result = result },
                WireJsonContext.Default.EvalResponse).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteJsonAsync(response, 200,
                Error(WireErrorCodes.EvalTimeout, $"Script evaluation timed out after {timeoutMs} ms."),
                WireJsonContext.Default.EvalResponse).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(response, 200,
                Error(WireErrorCodes.EvalFailed, $"{ex.GetType().Name}: {ex.Message}"),
                WireJsonContext.Default.EvalResponse).ConfigureAwait(false);
        }
        finally
        {
            registration.EvalLock.Release();
        }
    }

    private async Task HandleScreenshotAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        var body = await ReadJsonBodyAsync(request, WireJsonContext.Default.ScreenshotRequest).ConfigureAwait(false);
        if (body?.Target is null || !_targets.TryGetValue(body.Target, out var registration))
        {
            await WriteJsonAsync(response, 200, new ScreenshotResponse
            {
                Ok = false,
                Error = new WireError { Code = WireErrorCodes.UnknownTarget, Message = $"No webview is attached under the name '{body?.Target}'." },
            }, WireJsonContext.Default.ScreenshotResponse).ConfigureAwait(false);
            return;
        }

        if (registration.ScriptHost is not IScreenshotProvider screenshotProvider)
        {
            await WriteJsonAsync(response, 200, new ScreenshotResponse
            {
                Ok = false,
                Error = new WireError { Code = WireErrorCodes.Unsupported, Message = "This target's script host does not implement IScreenshotProvider." },
            }, WireJsonContext.Default.ScreenshotResponse).ConfigureAwait(false);
            return;
        }

        ScreenshotRegion? region = body.Rect is { } rect
            ? new ScreenshotRegion(rect.X, rect.Y, rect.Width, rect.Height)
            : null;
        var png = await screenshotProvider.TakePngScreenshotAsync(region, _shutdown.Token).ConfigureAwait(false);
        await WriteJsonAsync(response, 200, new ScreenshotResponse { Ok = true, DataBase64 = Convert.ToBase64String(png) },
            WireJsonContext.Default.ScreenshotResponse).ConfigureAwait(false);
    }

    private static EvalResponse Error(string code, string message)
        => new() { Ok = false, Error = new WireError { Code = code, Message = message } };

    private static async Task<T?> ReadJsonBodyAsync<T>(HttpListenerRequest request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        using var buffer = new MemoryStream();
        var capped = new byte[81920];
        int read;
        while ((read = await request.InputStream.ReadAsync(capped).ConfigureAwait(false)) > 0)
        {
            buffer.Write(capped, 0, read);
            if (buffer.Length > WireProtocol.MaxRequestBytes)
            {
                return null;
            }
        }

        try
        {
            buffer.Position = 0;
            return JsonSerializer.Deserialize(buffer, typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task WriteJsonAsync<T>(HttpListenerResponse response, int statusCode, T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = payload.Length;
        await response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
        response.Close();
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // Already stopped.
        }

        ((IDisposable)_listener).Dispose();
    }
}

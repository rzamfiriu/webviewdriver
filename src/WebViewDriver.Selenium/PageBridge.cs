using System.Text.Json.Nodes;
using OpenQA.Selenium;

namespace WebViewDriver.Selenium;

internal sealed record BridgeError(string Code, string Message, string? Stack);

internal sealed record BridgeResult(JsonNode? Value, BridgeError? Error)
{
    public bool IsError => Error is not null;
}

/// <summary>
/// Runs command scripts inside the page through the app's eval endpoint:
/// injects the bridge lazily (and again after navigations), unwraps envelopes,
/// and polls promise-backed ("pending") results.
/// </summary>
internal sealed class PageBridge
{
    private const string NoBridgeSentinel = "\"__wvd_nobridge\"";

    private static readonly string BridgeSource = LoadBridgeSource();

    private readonly WireClient _wire;
    private readonly string _target;

    public PageBridge(WireClient wire, string target)
    {
        _wire = wire;
        _target = target;
    }

    private static string LoadBridgeSource()
    {
        using var stream = typeof(PageBridge).Assembly.GetManifestResourceStream("WebViewDriver.Selenium.bridge.js")
                           ?? throw new InvalidOperationException("Embedded bridge.js resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Executes a function body in the page with the given W3C-shaped arguments.
    /// </summary>
    /// <param name="fnBody">JavaScript function body; receives <c>arguments</c>.</param>
    /// <param name="args">JSON array of arguments (element refs as W3C dictionaries).</param>
    /// <param name="asAsyncScript">Appends a resolve callback as the final argument (executeAsyncScript semantics).</param>
    /// <param name="scriptTimeout">Deadline for promise/async completion.</param>
    public async Task<BridgeResult> RunAsync(string fnBody, JsonArray args, bool asAsyncScript, TimeSpan scriptTimeout, CancellationToken cancellationToken)
    {
        var options = new JsonObject { ["async"] = asAsyncScript };
        var call = BuildExecScript(fnBody, args, options);

        var raw = await EvalWithInjectionAsync(call, cancellationToken).ConfigureAwait(false);
        var envelope = ParseEnvelope(raw);

        var deadline = DateTime.UtcNow + scriptTimeout;
        while (IsPending(envelope, out var pendingId))
        {
            if (DateTime.UtcNow > deadline)
            {
                return new BridgeResult(null, new BridgeError(
                    asAsyncScript ? "script timeout" : "script timeout",
                    $"Script did not complete within {scriptTimeout.TotalMilliseconds:F0} ms.", null));
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            var pollScript = $"(function(){{ if (!window.__wvd1) return '{{\"s\":\"error\",\"error\":\"javascript error\",\"message\":\"The page navigated while an async script was running.\"}}'; return window.__wvd1.poll({pendingId}); }})()";
            raw = await _wire.EvalAsync(_target, pollScript, timeoutMs: 10_000, cancellationToken).ConfigureAwait(false);
            envelope = ParseEnvelope(raw);
        }

        var status = (string?)envelope["s"];
        if (status == "ok")
        {
            return new BridgeResult(envelope["v"], null);
        }

        return new BridgeResult(null, new BridgeError(
            (string?)envelope["error"] ?? "javascript error",
            (string?)envelope["message"] ?? "Unknown script error.",
            (string?)envelope["stack"]));
    }

    /// <summary>Evaluates a raw script with no bridge involvement (used for navigation probes).</summary>
    public Task<string> EvalRawAsync(string script, CancellationToken cancellationToken)
        => _wire.EvalAsync(_target, script, timeoutMs: 30_000, cancellationToken);

    private static string BuildExecScript(string fnBody, JsonArray args, JsonObject options)
    {
        // fnBody and args travel as JSON string literals so no escaping issues arise.
        var fnBodyLiteral = JsonValue.Create(fnBody).ToJsonString();
        var argsLiteral = JsonValue.Create(args.ToJsonString()).ToJsonString();
        return $"(function(){{ if (!window.__wvd1) return '{NoBridgeSentinel.Replace("\"", "\\\"")}'; " +
               $"return window.__wvd1.exec({fnBodyLiteral}, {argsLiteral}, {options.ToJsonString()}); }})()";
    }

    private async Task<string> EvalWithInjectionAsync(string script, CancellationToken cancellationToken)
    {
        var raw = await _wire.EvalAsync(_target, script, timeoutMs: 90_000, cancellationToken).ConfigureAwait(false);
        if (raw != NoBridgeSentinel && !raw.Contains("__wvd_nobridge", StringComparison.Ordinal))
        {
            return raw;
        }

        await InjectBridgeAsync(cancellationToken).ConfigureAwait(false);
        raw = await _wire.EvalAsync(_target, script, timeoutMs: 90_000, cancellationToken).ConfigureAwait(false);
        if (raw.Contains("__wvd_nobridge", StringComparison.Ordinal))
        {
            throw new WebDriverException("The WebViewDriver bridge could not be installed in the page (eval succeeded but window.__wvd1 stayed undefined). " +
                                         "Check that the webview allows JavaScript evaluation.");
        }

        return raw;
    }

    public Task InjectBridgeAsync(CancellationToken cancellationToken)
        => _wire.EvalAsync(_target, BridgeSource + "; 'ok'", timeoutMs: 30_000, cancellationToken);

    /// <summary>
    /// Resets the browsing context to top-level without going through exec, so it
    /// works even when the currently selected frame's realm is gone.
    /// </summary>
    public Task ResetContextAsync(CancellationToken cancellationToken)
        => _wire.EvalAsync(_target, "(function(){ if (window.__wvd1) { window.__wvd1.ctx = null; } return 'ok'; })()", timeoutMs: 30_000, cancellationToken);

    private static JsonNode ParseEnvelope(string raw)
    {
        try
        {
            // Hosts may hand back the JSON either verbatim or re-quoted; normalize.
            var node = JsonNode.Parse(raw);
            if (node is JsonValue value && value.TryGetValue<string>(out var inner))
            {
                node = JsonNode.Parse(inner);
            }

            return node ?? throw new WebDriverException("The bridge returned an empty envelope.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new WebDriverException($"The bridge returned a malformed envelope: {Truncate(raw)}", ex);
        }
    }

    private static bool IsPending(JsonNode envelope, out long pendingId)
    {
        if ((string?)envelope["s"] == "pending")
        {
            pendingId = (long?)envelope["id"] ?? 0;
            return true;
        }

        pendingId = 0;
        return false;
    }

    private static string Truncate(string value)
        => value.Length <= 200 ? value : value[..200] + "…";
}

using System.Diagnostics;
using System.Text.Json.Nodes;
using OpenQA.Selenium;
using WebViewDriver.Selenium.Actions;

namespace WebViewDriver.Selenium;

/// <summary>
/// The smart half of the "dumb host, smart client" design: implements Selenium's
/// <see cref="ICommandExecutor"/> by mapping WebDriver commands onto command
/// scripts executed through the in-app eval endpoint.
/// </summary>
internal sealed class BridgeCommandExecutor : ICommandExecutor
{
    internal const string ElementKey = "element-6066-11e4-a52e-4f735466cecf";
    internal const string ShadowKey = "shadow-6066-11e4-a52e-4f735466cecf";

    private readonly WireClient _wire;
    private readonly PageBridge _bridge;
    private readonly string _target;
    private readonly PageLoadStrategy _pageLoadStrategy;
    private readonly Action<string>? _log;
    private readonly IActionsBackend _actionsBackend;
    private readonly IActionsContext _actionsContext;

    private string? _sessionId;
    private TimeSpan _implicitWait = TimeSpan.Zero;
    private TimeSpan _pageLoadTimeout = TimeSpan.FromSeconds(60);
    private TimeSpan _scriptTimeout = TimeSpan.FromSeconds(30);

    public BridgeCommandExecutor(WireClient wire, string target, PageLoadStrategy pageLoadStrategy = PageLoadStrategy.Normal, Action<string>? log = null, IActionsBackend? actionsBackend = null)
    {
        _wire = wire;
        _target = target;
        _pageLoadStrategy = pageLoadStrategy == PageLoadStrategy.Default ? PageLoadStrategy.Normal : pageLoadStrategy;
        _log = log;
        _actionsBackend = actionsBackend ?? new DomActionsBackend();
        _bridge = new PageBridge(wire, target);
        _actionsContext = new BridgeActionsContext(this);
    }

    /// <summary>Gives Actions backends page access with typed error surfacing.</summary>
    private sealed class BridgeActionsContext : IActionsContext
    {
        private readonly BridgeCommandExecutor _executor;

        public BridgeActionsContext(BridgeCommandExecutor executor) => _executor = executor;

        public async Task<JsonNode?> ExecuteScriptAsync(string fnBody, JsonArray args, CancellationToken cancellationToken)
        {
            var result = await _executor._bridge
                .RunAsync(fnBody, args, asAsyncScript: false, _executor._scriptTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (result.IsError)
            {
                throw ToException(result.Error!);
            }

            return result.Value;
        }

        private static WebDriverException ToException(BridgeError error)
        {
            var message = error.Stack is null ? error.Message : $"{error.Message}\nJS stack: {error.Stack}";
            return error.Code switch
            {
                "stale element reference" => new StaleElementReferenceException(message),
                "detached shadow root" => new StaleElementReferenceException(message),
                "no such window" => new NoSuchWindowException(message),
                "no such frame" => new NoSuchFrameException(message),
                "element not interactable" => new ElementNotInteractableException(message),
                "script timeout" => new WebDriverTimeoutException(message),
                _ => new WebDriverException(message),
            };
        }
    }

    public bool TryAddCommand(string commandName, CommandInfo? info) => false;

    public Response Execute(Command commandToExecute)
        => Task.Run(() => ExecuteAsync(commandToExecute)).GetAwaiter().GetResult();

    public async Task<Response> ExecuteAsync(Command commandToExecute)
    {
        var stopwatch = _log is null ? null : Stopwatch.StartNew();
        try
        {
            var response = await DispatchAsync(commandToExecute, CancellationToken.None).ConfigureAwait(false);
            _log?.Invoke($"[wvd] {commandToExecute.Name} -> {response.Status} in {stopwatch!.ElapsedMilliseconds} ms");
            return response;
        }
        catch (WebViewDriverConnectionException ex)
        {
            _log?.Invoke($"[wvd] {commandToExecute.Name} -> transport failure in {stopwatch?.ElapsedMilliseconds} ms: {ex.Message}");
            throw; // transport taxonomy is already descriptive; don't wrap
        }
        catch (WebDriverException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[wvd] {commandToExecute.Name} -> executor failure: {ex.Message}");
            return ErrorResponse(WebDriverResult.UnknownError, $"WebViewDriver executor failure on '{commandToExecute.Name}': {ex.Message}");
        }
    }

    private async Task<Response> DispatchAsync(Command command, CancellationToken ct)
    {
        var p = command.Parameters ?? new Dictionary<string, object?>();
        switch (command.Name)
        {
            case "newSession":
                _sessionId = Guid.NewGuid().ToString();
                return Success(new Dictionary<string, object?>
                {
                    ["browserName"] = "wkwebview",
                    ["platformName"] = "mac",
                    ["pageLoadStrategy"] = _pageLoadStrategy.ToString().ToLowerInvariant(),
                    ["webviewdriver:target"] = _target,
                    ["webviewdriver:protocolVersion"] = WireClient.SupportedProtocolVersion,
                });

            case "quit":
                return Success(null);

            case "status":
                return Success(new Dictionary<string, object?> { ["ready"] = true, ["message"] = "WebViewDriver bridge ready." });

            // -- Navigation ------------------------------------------------
            case "get":
                return await NavigateAsync(Scripts.NavigateTo, new JsonArray(GetString(p, "url")), GetString(p, "url"), allowNoOp: false, ct).ConfigureAwait(false);
            case "refresh":
                return await NavigateAsync(Scripts.Refresh, new JsonArray(), null, allowNoOp: false, ct).ConfigureAwait(false);
            case "goBack":
                return await NavigateAsync(Scripts.GoBack, new JsonArray(), null, allowNoOp: true, ct).ConfigureAwait(false);
            case "goForward":
                return await NavigateAsync(Scripts.GoForward, new JsonArray(), null, allowNoOp: true, ct).ConfigureAwait(false);

            case "getCurrentUrl":
                return await RunScalarAsync(Scripts.GetUrl, new JsonArray(), ct).ConfigureAwait(false);
            case "getTitle":
                return await RunScalarAsync(Scripts.GetTitle, new JsonArray(), ct).ConfigureAwait(false);
            case "getPageSource":
                return await RunScalarAsync(Scripts.GetPageSource, new JsonArray(), ct).ConfigureAwait(false);

            // -- Finds -----------------------------------------------------
            case "findElement":
                return await FindAsync(p, root: null, all: false, ct).ConfigureAwait(false);
            case "findElements":
                return await FindAsync(p, root: null, all: true, ct).ConfigureAwait(false);
            case "findChildElement":
                return await FindAsync(p, root: ElementRef(GetString(p, "id")!), all: false, ct).ConfigureAwait(false);
            case "findChildElements":
                return await FindAsync(p, root: ElementRef(GetString(p, "id")!), all: true, ct).ConfigureAwait(false);
            case "findShadowChildElement":
                return await FindAsync(p, root: ShadowRef(GetString(p, "id")!), all: false, ct).ConfigureAwait(false);
            case "findShadowChildElements":
                return await FindAsync(p, root: ShadowRef(GetString(p, "id")!), all: true, ct).ConfigureAwait(false);
            case "getActiveElement":
                return await RunScalarAsync(Scripts.GetActiveElement, new JsonArray(), ct).ConfigureAwait(false);

            // -- Frames and shadow DOM ---------------------------------------
            case "switchToFrame":
                return await SwitchToFrameAsync(p, ct).ConfigureAwait(false);
            case "switchToParentFrame":
                return await RunScalarAsync(Scripts.SwitchParentFrame, new JsonArray(), ct).ConfigureAwait(false);
            case "getElementShadowRoot":
                return await RunElementAsync(Scripts.GetShadowRoot, p, ct).ConfigureAwait(false);

            // -- Element interaction ----------------------------------------
            case "clickElement":
                return await RunElementAsync(Scripts.Click, p, ct).ConfigureAwait(false);
            case "clearElement":
                return await RunElementAsync(Scripts.Clear, p, ct).ConfigureAwait(false);
            case "sendKeysToElement":
                return await RunElementAsync(Scripts.SendKeys, p, ct, ExtractKeysText(p)).ConfigureAwait(false);

            // -- Element state ----------------------------------------------
            case "getElementText":
                return await RunElementAsync(Scripts.GetText, p, ct).ConfigureAwait(false);
            case "getElementAttribute":
                return await RunElementAsync(Scripts.GetDomAttribute, p, ct, GetString(p, "name")).ConfigureAwait(false);
            case "getElementProperty":
                return await RunElementAsync(Scripts.GetProperty, p, ct, GetString(p, "name")).ConfigureAwait(false);
            case "getElementValueOfCssProperty":
                return await RunElementAsync(Scripts.GetCssValue, p, ct, GetString(p, "propertyName") ?? GetString(p, "name")).ConfigureAwait(false);
            case "getElementRect":
                return await RunElementAsync(Scripts.GetRect, p, ct).ConfigureAwait(false);
            case "getElementTagName":
                return await RunElementAsync(Scripts.GetTagName, p, ct).ConfigureAwait(false);
            case "isElementEnabled":
                return await RunElementAsync(Scripts.IsEnabled, p, ct).ConfigureAwait(false);
            case "isElementSelected":
                return await RunElementAsync(Scripts.IsSelected, p, ct).ConfigureAwait(false);
            case "isElementDisplayed":
                return await RunElementAsync(Scripts.IsDisplayed, p, ct).ConfigureAwait(false);

            // -- W3C Actions (FR8) -------------------------------------------
            case "actions":
            {
                var sequences = p.TryGetValue("actions", out var rawSequences) && rawSequences is not null
                    ? (JsonArray)(JsonValueConversion.FromClr(rawSequences) ?? new JsonArray())
                    : new JsonArray();
                await _actionsBackend.PerformAsync(sequences, _actionsContext, ct).ConfigureAwait(false);
                return Success(null);
            }

            case "cancelActions":
                await _actionsBackend.ReleaseAsync(_actionsContext, ct).ConfigureAwait(false);
                return Success(null);

            // -- Script execution -------------------------------------------
            case "executeScript":
                return await ExecuteScriptAsync(p, asAsync: false, ct).ConfigureAwait(false);
            case "executeAsyncScript":
                return await ExecuteScriptAsync(p, asAsync: true, ct).ConfigureAwait(false);

            // -- Screenshots -------------------------------------------------
            case "screenshot":
            {
                var png = await _wire.TakeScreenshotAsync(_target, rect: null, ct).ConfigureAwait(false);
                return Success(Convert.ToBase64String(png));
            }

            case "elementScreenshot":
                return await ElementScreenshotAsync(p, ct).ConfigureAwait(false);

            // -- Timeouts ----------------------------------------------------
            case "setTimeouts":
            {
                if (p.TryGetValue("implicit", out var implicitMs) && implicitMs is not null)
                    _implicitWait = TimeSpan.FromMilliseconds(Convert.ToDouble(implicitMs));
                if (p.TryGetValue("pageLoad", out var pageLoadMs) && pageLoadMs is not null)
                    _pageLoadTimeout = TimeSpan.FromMilliseconds(Convert.ToDouble(pageLoadMs));
                if (p.TryGetValue("script", out var scriptMs) && scriptMs is not null)
                    _scriptTimeout = TimeSpan.FromMilliseconds(Convert.ToDouble(scriptMs));
                return Success(null);
            }

            case "getTimeouts":
                return Success(new Dictionary<string, object?>
                {
                    ["implicit"] = (long)_implicitWait.TotalMilliseconds,
                    ["pageLoad"] = (long)_pageLoadTimeout.TotalMilliseconds,
                    ["script"] = (long)_scriptTimeout.TotalMilliseconds,
                });

            // -- Single-window model ----------------------------------------
            case "getCurrentWindowHandle":
                return Success(_target);
            case "getWindowHandles":
                return Success(new object[] { _target });
            case "switchToWindow":
                return GetString(p, "handle") == _target
                    ? Success(null)
                    : ErrorResponse(WebDriverResult.NoSuchWindow, $"WebViewDriver exposes a single window named '{_target}'.");
            case "getWindowRect":
                return await RunScalarAsync(Scripts.GetWindowRect, new JsonArray(), ct).ConfigureAwait(false);
            case "setWindowRect":
            case "maximizeWindow":
            case "minimizeWindow":
            case "fullScreenWindow":
            case "close":
                return Success(null); // single-window no-ops (FR2)

            default:
                return ErrorResponse(WebDriverResult.UnknownCommand,
                    $"WebViewDriver does not implement the '{command.Name}' command. " +
                    "Cookies, alerts, BiDi and virtual authenticators are out of scope; see the limitations page.");
        }
    }

    // -- Command helpers ---------------------------------------------------

    private async Task<Response> FindAsync(Dictionary<string, object?> p, JsonObject? root, bool all, CancellationToken ct)
    {
        var mechanism = GetString(p, "using") ?? "";
        var value = GetString(p, "value") ?? "";

        (string strategy, string selector) translated;
        try
        {
            translated = Scripts.TranslateLocator(mechanism, value);
        }
        catch (InvalidSelectorException ex)
        {
            return ErrorResponse(WebDriverResult.InvalidSelector, ex.Message);
        }

        var deadline = DateTime.UtcNow + _implicitWait;
        while (true)
        {
            var args = new JsonArray(translated.strategy, translated.selector, root?.DeepClone(), all);
            var result = await _bridge.RunAsync(Scripts.Find, args, asAsyncScript: false, _scriptTimeout, ct).ConfigureAwait(false);
            if (result.IsError)
            {
                return ErrorFromBridge(result.Error!);
            }

            if (all)
            {
                var list = result.Value as JsonArray;
                if (list is { Count: > 0 } || DateTime.UtcNow >= deadline)
                {
                    return Success(JsonValueConversion.ToClr(result.Value) ?? Array.Empty<object>());
                }
            }
            else if (result.Value is not null)
            {
                return Success(JsonValueConversion.ToClr(result.Value));
            }
            else if (DateTime.UtcNow >= deadline)
            {
                return ErrorResponse(WebDriverResult.NoSuchElement,
                    $"No element matched {mechanism} '{value}'" + (root is null ? "." : " under the given parent."));
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    private async Task<Response> SwitchToFrameAsync(Dictionary<string, object?> p, CancellationToken ct)
    {
        p.TryGetValue("id", out var frameId);
        if (frameId is null)
        {
            // Always works, even when the current frame realm is gone.
            await _bridge.ResetContextAsync(ct).ConfigureAwait(false);
            return Success(null);
        }

        JsonNode arg = frameId switch
        {
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            double d => JsonValue.Create(d),
            _ => JsonValueConversion.FromClr(frameId) ?? throw new WebDriverException("Unrecognized switchToFrame argument."),
        };

        var result = await _bridge.RunAsync(Scripts.SwitchFrame, new JsonArray(arg), asAsyncScript: false, _scriptTimeout, ct).ConfigureAwait(false);
        return result.IsError ? ErrorFromBridge(result.Error!) : Success(null);
    }

    private async Task<Response> ElementScreenshotAsync(Dictionary<string, object?> p, CancellationToken ct)
    {
        var elementId = GetString(p, "id") ?? throw new WebDriverException("Element screenshot without an element id.");
        var rectResult = await _bridge.RunAsync(Scripts.GetViewportRect, new JsonArray(ElementRef(elementId)), asAsyncScript: false, _scriptTimeout, ct).ConfigureAwait(false);
        if (rectResult.IsError)
        {
            return ErrorFromBridge(rectResult.Error!);
        }

        var rect = new JsonObject
        {
            ["x"] = (double?)rectResult.Value?["x"] ?? 0,
            ["y"] = (double?)rectResult.Value?["y"] ?? 0,
            ["width"] = (double?)rectResult.Value?["width"] ?? 0,
            ["height"] = (double?)rectResult.Value?["height"] ?? 0,
        };
        var png = await _wire.TakeScreenshotAsync(_target, rect, ct).ConfigureAwait(false);
        return Success(Convert.ToBase64String(png));
    }

    private async Task<Response> ExecuteScriptAsync(Dictionary<string, object?> p, bool asAsync, CancellationToken ct)
    {
        var script = GetString(p, "script") ?? "";
        var args = p.TryGetValue("args", out var rawArgs) && rawArgs is not null
            ? (JsonArray)(JsonValueConversion.FromClr(rawArgs) ?? new JsonArray())
            : new JsonArray();

        var result = await _bridge.RunAsync(script, args, asAsync, _scriptTimeout, ct).ConfigureAwait(false);
        return result.IsError ? ErrorFromBridge(result.Error!, asAsync) : Success(JsonValueConversion.ToClr(result.Value));
    }

    private async Task<Response> RunElementAsync(string fnBody, Dictionary<string, object?> p, CancellationToken ct, string? extraArg = null)
    {
        var elementId = GetString(p, "id") ?? throw new WebDriverException("Element command without an element id.");
        var args = new JsonArray(ElementRef(elementId));
        if (extraArg is not null)
        {
            args.Add(extraArg);
        }

        var result = await _bridge.RunAsync(fnBody, args, asAsyncScript: false, _scriptTimeout, ct).ConfigureAwait(false);
        return result.IsError ? ErrorFromBridge(result.Error!) : Success(JsonValueConversion.ToClr(result.Value));
    }

    private async Task<Response> RunScalarAsync(string fnBody, JsonArray args, CancellationToken ct)
    {
        var result = await _bridge.RunAsync(fnBody, args, asAsyncScript: false, _scriptTimeout, ct).ConfigureAwait(false);
        return result.IsError ? ErrorFromBridge(result.Error!) : Success(JsonValueConversion.ToClr(result.Value));
    }

    // -- Navigation ----------------------------------------------------------

    private sealed record NavigationState(string? DocId, string? Ready, string? Url);

    private async Task<NavigationState?> ReadNavigationStateAsync(CancellationToken ct)
    {
        try
        {
            var state = await _bridge.RunAsync(Scripts.ReadNavigationState, new JsonArray(), asAsyncScript: false, _scriptTimeout, ct).ConfigureAwait(false);
            if (state.IsError)
            {
                return null;
            }

            return new NavigationState(
                (string?)state.Value?["docId"],
                (string?)state.Value?["ready"],
                (string?)state.Value?["url"]);
        }
        catch (WebViewDriverConnectionException)
        {
            return null; // webview busy mid-navigation
        }
    }

    /// <summary>
    /// Waits on document identity: a fresh document (or bfcache restore of a
    /// different one) changes the bridge docId; same-document navigations (hash
    /// changes) keep it and are detected by URL change instead.
    /// </summary>
    private async Task<Response> NavigateAsync(string fnBody, JsonArray args, string? expectedUrl, bool allowNoOp, CancellationToken ct)
    {
        var before = await ReadNavigationStateAsync(ct).ConfigureAwait(false);

        var kickoff = await _bridge.RunAsync(fnBody, args, asAsyncScript: false, _scriptTimeout, ct).ConfigureAwait(false);
        if (kickoff.IsError)
        {
            return ErrorFromBridge(kickoff.Error!);
        }

        if (_pageLoadStrategy == PageLoadStrategy.None)
        {
            return Success(null);
        }

        var started = DateTime.UtcNow;
        var deadline = started + _pageLoadTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, ct).ConfigureAwait(false);
            var state = await ReadNavigationStateAsync(ct).ConfigureAwait(false);
            if (state is null)
            {
                continue;
            }

            var ready = state.Ready == "complete"
                        || (_pageLoadStrategy == PageLoadStrategy.Eager && state.Ready == "interactive");
            if (!ready)
            {
                continue;
            }

            var documentChanged = before?.DocId is null || state.DocId != before.DocId;
            var urlChanged = state.Url != before?.Url;
            var urlMatchesTarget = expectedUrl is not null && UrlsMatch(state.Url, expectedUrl);

            if (documentChanged || urlMatchesTarget || (expectedUrl is null && urlChanged))
            {
                return Success(null);
            }

            // back/forward with no history entry is a spec no-op: accept the
            // unchanged state after a settle period.
            if (allowNoOp && DateTime.UtcNow - started > TimeSpan.FromMilliseconds(750))
            {
                return Success(null);
            }
        }

        return ErrorResponse(WebDriverResult.Timeout,
            $"Navigation did not complete within {_pageLoadTimeout.TotalSeconds:F0} s.");
    }

    private static bool UrlsMatch(string? current, string expected)
        => current is not null &&
           (string.Equals(current, expected, StringComparison.Ordinal)
            || string.Equals(current.TrimEnd('/'), expected.TrimEnd('/'), StringComparison.Ordinal));

    private static string? ExtractKeysText(Dictionary<string, object?> p)
    {
        if (p.TryGetValue("text", out var text) && text is string s)
        {
            return s;
        }

        if (p.TryGetValue("value", out var value) && value is IEnumerable<object?> chars)
        {
            return string.Concat(chars.Select(c => c?.ToString()));
        }

        return "";
    }

    // -- Response plumbing ---------------------------------------------------

    private static JsonObject ElementRef(string elementId) => new() { [ElementKey] = elementId };

    private static JsonObject ShadowRef(string shadowId) => new() { [ShadowKey] = shadowId };

    private static string? GetString(Dictionary<string, object?> parameters, string key)
        => parameters.TryGetValue(key, out var value) ? value?.ToString() : null;

    private Response Success(object? value) => new(_sessionId, value, WebDriverResult.Success);

    private Response ErrorResponse(WebDriverResult status, string message)
        => new(_sessionId, new Dictionary<string, object?> { ["message"] = message }, status);

    private Response ErrorFromBridge(BridgeError error, bool asAsyncScript = false)
    {
        var status = error.Code switch
        {
            "stale element reference" => WebDriverResult.ObsoleteElement,
            "detached shadow root" => WebDriverResult.DetachedShadowRoot,
            "no such shadow root" => WebDriverResult.NoSuchShadowRoot,
            "no such element" => WebDriverResult.NoSuchElement,
            "no such frame" => WebDriverResult.NoSuchFrame,
            "no such window" => WebDriverResult.NoSuchWindow,
            "invalid selector" => WebDriverResult.InvalidSelector,
            "script timeout" => asAsyncScript ? WebDriverResult.AsyncScriptTimeout : WebDriverResult.Timeout,
            "element not interactable" => WebDriverResult.ElementNotInteractable,
            "element click intercepted" => WebDriverResult.ElementClickIntercepted,
            "javascript error" => WebDriverResult.UnexpectedJavaScriptError,
            _ => WebDriverResult.UnknownError,
        };

        var message = error.Stack is null ? error.Message : $"{error.Message}\nJS stack: {error.Stack}";
        return ErrorResponse(status, message);
    }

    internal Task<Response> WarmUpAsync(CancellationToken ct) => RunScalarAsync("return document.readyState;", new JsonArray(), ct);

    internal Task<Response> RunReadinessAsync(string readinessScript, CancellationToken ct)
        => RunScalarAsync(readinessScript, new JsonArray(), ct);

    public void Dispose() => _wire.Dispose();
}

using System.Text.Json.Nodes;
using OpenQA.Selenium;
using OpenQA.Selenium.Remote;

namespace WebViewDriver.Selenium;

/// <summary>Describes a webview target attached to an app's automation endpoint.</summary>
/// <param name="Name">The name the app used in WebViewDriverHost.Attach.</param>
/// <param name="Screenshots">Whether the target's script host supports screenshots.</param>
/// <param name="Ready">App-declared readiness (FR9); null when never declared.</param>
public sealed record WebViewTargetInfo(string Name, bool Screenshots, bool? Ready);

/// <summary>
/// Entry point for tests: connects to an app that hosts the WebViewDriver
/// endpoint and returns a standard Selenium <see cref="IWebDriver"/>
/// (a <see cref="RemoteWebDriver"/>), so WebDriverWait, ExpectedConditions and
/// page objects work unchanged.
/// </summary>
public static class WebViewDriverClient
{
    /// <summary>
    /// Connects to the app's automation endpoint, waiting until the named
    /// webview target is attached, app-declared-ready (if declared), and its
    /// document is interactive.
    /// </summary>
    /// <param name="port">The value passed to the app as WEBVIEWDRIVER_PORT.</param>
    /// <param name="token">The value passed to the app as WEBVIEWDRIVER_TOKEN.</param>
    /// <param name="target">The name the app used in WebViewDriverHost.Attach.</param>
    /// <param name="timeout">How long to wait for the endpoint and target; default 30 s.</param>
    public static IWebDriver Connect(int port, string token, string target = "main", TimeSpan? timeout = null)
        => Connect(port, token, target, new WebViewDriverClientOptions { Timeout = timeout ?? TimeSpan.FromSeconds(30) });

    /// <summary>Connects with diagnostics, Actions-backend and readiness options.</summary>
    public static IWebDriver Connect(int port, string token, string target, WebViewDriverClientOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentNullException.ThrowIfNull(options);
        var deadline = DateTime.UtcNow + options.Timeout;

        var wire = new WireClient(port, token, options.WireCaptureFile);
        try
        {
            WaitForTarget(wire, target, deadline);

            var executor = new BridgeCommandExecutor(wire, target, options.PageLoadStrategy, options.Log, options.ActionsBackend);
            WaitForDocument(executor, deadline);
            if (options.ReadinessScript is not null)
            {
                WaitForReadinessScript(executor, options.ReadinessScript, deadline);
            }

            return new RemoteWebDriver(executor, new RemoteSessionSettings());
        }
        catch
        {
            wire.Dispose();
            throw;
        }
    }

    /// <summary>Lists the webview targets currently attached to the endpoint.</summary>
    public static IReadOnlyList<WebViewTargetInfo> ListTargets(int port, string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        using var wire = new WireClient(port, token);
        var status = wire.GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult();
        return ParseTargets(status);
    }

    private static List<WebViewTargetInfo> ParseTargets(JsonNode status)
        => status["targets"]?.AsArray()
               .Where(t => t?["name"] is not null)
               .Select(t => new WebViewTargetInfo(
                   (string)t!["name"]!,
                   (bool?)t["screenshots"] ?? false,
                   (bool?)t["ready"]))
               .ToList()
           ?? new List<WebViewTargetInfo>();

    private static void WaitForTarget(WireClient wire, string target, DateTime deadline)
    {
        Exception? lastFailure = null;
        var seenTargets = new List<WebViewTargetInfo>();
        var sawNotReady = false;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var status = wire.GetStatusAsync(CancellationToken.None).GetAwaiter().GetResult();
                seenTargets = ParseTargets(status);
                var match = seenTargets.FirstOrDefault(t => t.Name == target);
                if (match is not null)
                {
                    // FR9: a target that declared itself not-ready is not connectable yet.
                    if (match.Ready != false)
                    {
                        return;
                    }

                    sawNotReady = true;
                }

                lastFailure = null;
            }
            catch (WebViewDriverConnectionException ex)
            {
                lastFailure = ex;
            }

            Thread.Sleep(150);
        }

        if (lastFailure is not null)
        {
            throw new WebViewDriverConnectionException(
                $"Timed out waiting for the WebViewDriver endpoint: {lastFailure.Message}", lastFailure);
        }

        if (sawNotReady)
        {
            throw new WebViewDriverConnectionException(
                $"The webview '{target}' is attached but the app never declared it ready " +
                "(WebViewDriverHost.SetReady was called with false and never with true).");
        }

        throw new WebViewDriverConnectionException(
            $"The WebViewDriver endpoint is up, but no webview named '{target}' attached in time. " +
            (seenTargets.Count > 0
                ? $"Attached targets: {string.Join(", ", seenTargets.Select(t => t.Name))}."
                : "No targets are attached; check that the app calls WebViewDriverHost.Attach after the webview is created."));
    }

    private static void WaitForDocument(BridgeCommandExecutor executor, DateTime deadline)
    {
        Exception? lastFailure = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = executor.WarmUpAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (response.Value is "interactive" or "complete")
                {
                    return;
                }
            }
            catch (WebDriverException ex)
            {
                lastFailure = ex;
            }

            Thread.Sleep(150);
        }

        throw new WebViewDriverConnectionException(
            "The webview target attached but its document never became interactive." +
            (lastFailure is null ? "" : $" Last error: {lastFailure.Message}"));
    }

    private static void WaitForReadinessScript(BridgeCommandExecutor executor, string readinessScript, DateTime deadline)
    {
        Exception? lastFailure = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = executor.RunReadinessAsync(readinessScript, CancellationToken.None).GetAwaiter().GetResult();
                if (response.Value is true)
                {
                    return;
                }

                lastFailure = null;
            }
            catch (WebDriverException ex)
            {
                lastFailure = ex;
            }

            Thread.Sleep(100);
        }

        throw new WebViewDriverConnectionException(
            "The readiness script never returned true within the connect timeout." +
            (lastFailure is null ? "" : $" Last error: {lastFailure.Message}"));
    }
}

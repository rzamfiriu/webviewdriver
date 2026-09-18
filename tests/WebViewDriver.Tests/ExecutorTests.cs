using OpenQA.Selenium;
using WebViewDriver.Host;
using WebViewDriver.Selenium;
using Xunit;

namespace WebViewDriver.Tests;

/// <summary>
/// Drives a real RemoteWebDriver + BridgeCommandExecutor against a scripted
/// fake webview, proving the error taxonomy and the lazy bridge injection
/// without any JS engine (QR2).
/// </summary>
public sealed class ExecutorTests : IDisposable
{
    private const string Token = "executor-test-token";

    private readonly HostServer _server;
    private readonly FakeScriptHost _webview = new();
    private bool _bridgeInjected;

    public ExecutorTests()
    {
        _server = new HostServer(Ports.GetFree(), Token);
        _server.Start();
        _server.AttachTarget("main", _webview);
        _webview.Responder = DefaultResponder;
    }

    /// <summary>Simulates the page: bridge missing until injected, readyState complete.</summary>
    private string? DefaultResponder(string script)
    {
        if (script.Contains("window.__wvd1 = bridge"))
        {
            _bridgeInjected = true;
            return "ok";
        }

        if (!_bridgeInjected)
        {
            return "\"__wvd_nobridge\"";
        }

        if (script.Contains("document.readyState"))
        {
            return """{"s":"ok","v":"complete"}""";
        }

        return """{"s":"ok","v":null}""";
    }

    private IWebDriver Connect() => WebViewDriverClient.Connect(_server.Port, Token, timeout: TimeSpan.FromSeconds(10));

    [Fact]
    public void Connect_reports_bad_token_clearly()
    {
        var ex = Assert.Throws<WebViewDriverConnectionException>(
            () => WebViewDriverClient.Connect(_server.Port, "wrong-token", timeout: TimeSpan.FromSeconds(2)));
        Assert.Contains("token", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Connect_reports_endpoint_down_clearly()
    {
        var ex = Assert.Throws<WebViewDriverConnectionException>(
            () => WebViewDriverClient.Connect(Ports.GetFree(), Token, timeout: TimeSpan.FromSeconds(2)));
        Assert.Contains("WEBVIEWDRIVER_PORT", ex.Message);
    }

    [Fact]
    public void Connect_reports_missing_target_and_lists_attached_ones()
    {
        var ex = Assert.Throws<WebViewDriverConnectionException>(
            () => WebViewDriverClient.Connect(_server.Port, Token, target: "settings", timeout: TimeSpan.FromSeconds(2)));
        Assert.Contains("main", ex.Message);
    }

    [Fact]
    public void Bridge_is_injected_lazily_then_command_retried()
    {
        using var driver = Connect();
        Assert.True(_bridgeInjected);
        Assert.Contains(_webview.ReceivedScripts, s => s.Contains("window.__wvd1 = bridge"));
        // The session is real: fabricated capabilities round-tripped.
        var capabilities = ((OpenQA.Selenium.Remote.RemoteWebDriver)driver).Capabilities;
        Assert.Equal("wkwebview", capabilities.GetCapability("browserName")?.ToString());
    }

    [Fact]
    public void Stale_reference_maps_to_typed_exception()
    {
        using var driver = Connect();
        RespondPerCommand(
            ("querySelectorAll", """{"s":"ok","v":{"element-6066-11e4-a52e-4f735466cecf":"wvd-1"}}"""),
            ("pointerdown", """{"s":"error","error":"stale element reference","message":"The element is no longer attached to the document.","stack":null}"""));

        var element = driver.FindElement(By.CssSelector("#gone"));
        var ex = Assert.Throws<StaleElementReferenceException>(element.Click);
        Assert.Contains("no longer attached", ex.Message);
    }

    [Fact]
    public void Missing_element_maps_to_NoSuchElementException()
    {
        using var driver = Connect();
        RespondPerCommand(("querySelectorAll", """{"s":"ok","v":null}"""));
        Assert.Throws<NoSuchElementException>(() => driver.FindElement(By.CssSelector("#missing")));
    }

    [Fact]
    public void Invalid_selector_maps_to_InvalidSelectorException()
    {
        using var driver = Connect();
        RespondPerCommand(("querySelectorAll", """{"s":"error","error":"invalid selector","message":"Invalid selector \"[\"","stack":null}"""));
        Assert.Throws<InvalidSelectorException>(() => driver.FindElement(By.CssSelector("[")));
    }

    [Fact]
    public void Js_error_in_ExecuteScript_maps_to_WebDriverException()
    {
        using var driver = Connect();
        RespondPerCommand(("boom", """{"s":"error","error":"javascript error","message":"boom","stack":"Error: boom"}"""));
        var ex = Assert.ThrowsAny<WebDriverException>(() => ((IJavaScriptExecutor)driver).ExecuteScript("throw new Error('boom');"));
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public void Implicit_wait_retries_find_until_deadline()
    {
        using var driver = Connect();
        driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromMilliseconds(400);

        var attempts = 0;
        RespondPerCommand(("querySelectorAll", () =>
        {
            attempts++;
            return attempts < 3
                ? """{"s":"ok","v":null}"""
                : """{"s":"ok","v":{"element-6066-11e4-a52e-4f735466cecf":"wvd-9"}}""";
        }));

        var element = driver.FindElement(By.CssSelector("#late"));
        Assert.NotNull(element);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void Async_script_polls_pending_result()
    {
        using var driver = Connect();
        var polls = 0;
        _webview.Responder = script =>
        {
            if (script.Contains(".poll("))
            {
                polls++;
                return polls < 2
                    ? """{"s":"pending","id":7}"""
                    : """{"s":"ok","v":42}""";
            }

            if (script.Contains("window.__wvd1.exec"))
            {
                return """{"s":"pending","id":7}""";
            }

            return DefaultResponder(script);
        };

        var result = ((IJavaScriptExecutor)driver).ExecuteAsyncScript("var cb = arguments[arguments.length - 1];");
        Assert.Equal(42L, result);
    }

    private sealed class RecordingActionsBackend : WebViewDriver.Selenium.Actions.IActionsBackend
    {
        public System.Text.Json.Nodes.JsonArray? LastSequences { get; private set; }
        public bool Released { get; private set; }

        public Task PerformAsync(System.Text.Json.Nodes.JsonArray sequences, WebViewDriver.Selenium.Actions.IActionsContext context, CancellationToken ct)
        {
            LastSequences = sequences;
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(WebViewDriver.Selenium.Actions.IActionsContext context, CancellationToken ct)
        {
            Released = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Custom_actions_backend_receives_w3c_sequences()
    {
        var backend = new RecordingActionsBackend();
        using var driver = WebViewDriverClient.Connect(_server.Port, Token, "main", new WebViewDriverClientOptions
        {
            Timeout = TimeSpan.FromSeconds(10),
            ActionsBackend = backend,
        });
        RespondPerCommand(("querySelectorAll", """{"s":"ok","v":{"element-6066-11e4-a52e-4f735466cecf":"wvd-1"}}"""));

        new OpenQA.Selenium.Interactions.Actions(driver)
            .MoveToElement(driver.FindElement(By.CssSelector("#x")), 0, 0)
            .Click()
            .Perform();
        ((OpenQA.Selenium.IActionExecutor)driver).ResetInputState();

        Assert.NotNull(backend.LastSequences);
        var pointer = backend.LastSequences!.First(s => (string?)s?["type"] == "pointer");
        var actionTypes = pointer!["actions"]!.AsArray().Select(a => (string?)a?["type"]).ToList();
        Assert.Contains("pointerMove", actionTypes);
        Assert.Contains("pointerDown", actionTypes);
        Assert.Contains("pointerUp", actionTypes);
        Assert.True(backend.Released);
    }

    [Fact]
    public void Switch_to_default_content_resets_context_even_without_exec()
    {
        using var driver = Connect();
        driver.SwitchTo().DefaultContent();
        Assert.Contains(_webview.ReceivedScripts, s => s.Contains("__wvd1.ctx = null"));
    }

    [Fact]
    public void No_such_frame_maps_to_typed_exception()
    {
        using var driver = Connect();
        RespondPerCommand(("window.frames", """{"s":"error","error":"no such frame","message":"No frame at index 7 (frame count: 0).","stack":null}"""));
        Assert.Throws<NoSuchFrameException>(() => driver.SwitchTo().Frame(7));
    }

    /// <summary>Routes eval scripts to canned envelopes by fnBody fingerprint, keeping the default behavior otherwise.</summary>
    private void RespondPerCommand(params (string Fingerprint, string Envelope)[] routes)
        => RespondPerCommand(routes.Select(r => (r.Fingerprint, (Func<string>)(() => r.Envelope))).ToArray());

    private void RespondPerCommand(params (string Fingerprint, Func<string> Envelope)[] routes)
    {
        _webview.Responder = script =>
        {
            foreach (var (fingerprint, envelope) in routes)
            {
                if (script.Contains(fingerprint))
                {
                    return envelope();
                }
            }

            return DefaultResponder(script);
        };
    }

    public void Dispose() => _server.Dispose();
}

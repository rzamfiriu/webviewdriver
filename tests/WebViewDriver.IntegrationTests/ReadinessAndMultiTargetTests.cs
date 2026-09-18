using OpenQA.Selenium;
using WebViewDriver.Host;
using WebViewDriver.Selenium;
using Xunit;

namespace WebViewDriver.IntegrationTests;

/// <summary>FR9 readiness + multi-target: second jsdom webviews attached to the shared host.</summary>
[Collection("driver")]
public sealed class ReadinessAndMultiTargetTests : IDisposable
{
    private readonly DriverFixture _fixture;
    private readonly List<(string Name, JsdomScriptHost Host)> _extraTargets = new();

    public ReadinessAndMultiTargetTests(DriverFixture fixture) => _fixture = fixture;

    private JsdomScriptHost AttachTarget(string name)
    {
        var jsdom = new JsdomScriptHost();
        WebViewDriverHost.Attach(name, jsdom);
        _extraTargets.Add((name, jsdom));
        return jsdom;
    }

    public void Dispose()
    {
        foreach (var (name, host) in _extraTargets)
        {
            WebViewDriverHost.Detach(name);
            host.Dispose();
        }
    }

    [Fact]
    public void ListTargets_reports_attached_webviews()
    {
        AttachTarget("list-me");
        var targets = WebViewDriverClient.ListTargets(_fixture.Port, DriverFixture.Token);
        Assert.Contains(targets, t => t.Name == "main");
        Assert.Contains(targets, t => t.Name == "list-me" && t.Ready is null);
    }

    [Fact]
    public void Connect_waits_for_app_declared_readiness()
    {
        AttachTarget("not-ready");
        WebViewDriverHost.SetReady("not-ready", false);

        var ex = Assert.Throws<WebViewDriverConnectionException>(() =>
            WebViewDriverClient.Connect(_fixture.Port, DriverFixture.Token, target: "not-ready", timeout: TimeSpan.FromSeconds(2)));
        Assert.Contains("never declared it ready", ex.Message);

        WebViewDriverHost.SetReady("not-ready", true);
        using var driver = WebViewDriverClient.Connect(_fixture.Port, DriverFixture.Token, target: "not-ready", timeout: TimeSpan.FromSeconds(10));
        Assert.Equal("WebViewDriver Fixture", driver.Title);
    }

    [Fact]
    public void Connect_honors_a_readiness_script()
    {
        AttachTarget("script-ready");

        // The fixture sets window.delayedFlag ~250 ms after load; a fresh jsdom
        // target is guaranteed to start out not-ready.
        using var driver = WebViewDriverClient.Connect(_fixture.Port, DriverFixture.Token, "script-ready", new WebViewDriverClientOptions
        {
            Timeout = TimeSpan.FromSeconds(15),
            ReadinessScript = "return window.delayedFlag === true;",
        });

        Assert.True((bool)((IJavaScriptExecutor)driver).ExecuteScript("return window.delayedFlag;")!);
    }

    [Fact]
    public void Two_targets_are_independent_sessions()
    {
        AttachTarget("second");
        using var second = WebViewDriverClient.Connect(_fixture.Port, DriverFixture.Token, target: "second", timeout: TimeSpan.FromSeconds(30));
        var main = _fixture.Driver;

        ((IJavaScriptExecutor)second).ExecuteScript("document.getElementById('heading').textContent = 'second target';");

        Assert.Equal("second target", second.FindElement(By.Id("heading")).Text);
        Assert.Equal("WebViewDriver fixture", main.FindElement(By.Id("heading")).Text);

        // Interleave commands across both sessions.
        for (var i = 0; i < 5; i++)
        {
            Assert.NotNull(main.FindElement(By.Id("save")));
            Assert.NotNull(second.FindElement(By.Id("save")));
        }
    }
}

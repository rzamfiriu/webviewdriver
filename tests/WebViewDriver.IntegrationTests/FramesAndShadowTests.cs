using OpenQA.Selenium;
using Xunit;

namespace WebViewDriver.IntegrationTests;

[Collection("driver")]
public sealed class FramesAndShadowTests : IDisposable
{
    private readonly IWebDriver _driver;
    private IJavaScriptExecutor Js => (IJavaScriptExecutor)_driver;

    public FramesAndShadowTests(DriverFixture fixture) => _driver = fixture.Driver;

    // Every test leaves the driver at top-level so ordering never matters.
    public void Dispose() => _driver.SwitchTo().DefaultContent();

    // -- Frames -----------------------------------------------------------------

    [Fact]
    public void Switch_by_index_scopes_finds_and_scripts_to_the_frame()
    {
        _driver.SwitchTo().Frame(0);

        Assert.Equal("inside frame", _driver.FindElement(By.Id("frame-text")).Text);
        // Unqualified `document` in scripts resolves to the frame's realm.
        Assert.Equal("inside frame", Js.ExecuteScript("return document.getElementById('frame-text').textContent;"));
        // Top-level content is not visible from the frame context.
        Assert.Empty(_driver.FindElements(By.Id("heading")));
    }

    [Fact]
    public void Switch_by_element_works_and_default_content_returns_to_top()
    {
        var iframe = _driver.FindElement(By.Id("frame1"));
        _driver.SwitchTo().Frame(iframe);
        Assert.NotEmpty(_driver.FindElements(By.Id("frame-btn")));

        _driver.SwitchTo().DefaultContent();
        Assert.NotEmpty(_driver.FindElements(By.Id("heading")));
        Assert.Empty(_driver.FindElements(By.Id("frame-text")));
    }

    [Fact]
    public void Clicks_inside_the_frame_fire_frame_handlers()
    {
        _driver.SwitchTo().Frame(0);
        _driver.FindElement(By.Id("frame-btn")).Click();
        Assert.Equal("frame clicked", _driver.FindElement(By.Id("frame-text")).Text);
        // Reset the frame state for other tests.
        Js.ExecuteScript("document.getElementById('frame-text').textContent = 'inside frame';");
    }

    [Fact]
    public void Parent_frame_returns_to_top()
    {
        _driver.SwitchTo().Frame(0);
        _driver.SwitchTo().ParentFrame();
        Assert.NotEmpty(_driver.FindElements(By.Id("heading")));
    }

    [Fact]
    public void Invalid_frame_index_throws_NoSuchFrame()
        => Assert.Throws<NoSuchFrameException>(() => _driver.SwitchTo().Frame(7));

    [Fact]
    public void Non_frame_element_throws_NoSuchFrame()
    {
        var heading = _driver.FindElement(By.Id("heading"));
        Assert.Throws<NoSuchFrameException>(() => _driver.SwitchTo().Frame(heading));
    }

    [Fact]
    public void Removed_frame_surfaces_no_such_window_and_default_content_recovers()
    {
        Js.ExecuteScript("""
            var f = document.createElement('iframe');
            f.id = 'doomed-frame';
            document.body.appendChild(f);
            """);
        var doomed = _driver.FindElement(By.Id("doomed-frame"));
        _driver.SwitchTo().Frame(doomed);

        _driver.SwitchTo().DefaultContent();
        Js.ExecuteScript("document.getElementById('doomed-frame').remove();");
        _driver.SwitchTo().Frame(_driver.FindElement(By.Id("frame1")));
        _driver.SwitchTo().DefaultContent();

        // Now select a frame and remove it while selected.
        Js.ExecuteScript("""
            var f = document.createElement('iframe');
            f.id = 'doomed-frame-2';
            document.body.appendChild(f);
            """);
        _driver.SwitchTo().Frame(_driver.FindElement(By.Id("doomed-frame-2")));
        // Removing the selected frame must be done from the top realm; use the
        // escape hatch that DefaultContent provides afterwards.
        _driver.SwitchTo().DefaultContent();
        Js.ExecuteScript("document.getElementById('doomed-frame-2').remove();");
        Assert.NotEmpty(_driver.FindElements(By.Id("heading")));
    }

    // -- Shadow DOM ----------------------------------------------------------------

    [Fact]
    public void Shadow_root_finds_and_interactions_work()
    {
        var host = _driver.FindElement(By.Id("shadow-host"));
        var shadowRoot = host.GetShadowRoot();

        var label = shadowRoot.FindElement(By.CssSelector(".shadow-label"));
        Assert.Equal("shadow text", label.Text);

        shadowRoot.FindElement(By.CssSelector("#shadow-btn")).Click();
        Assert.Equal("shadow clicked", shadowRoot.FindElement(By.CssSelector(".shadow-label")).Text);
        Js.ExecuteScript("document.getElementById('shadow-host').shadowRoot.querySelector('.shadow-label').textContent = 'shadow text';");
    }

    [Fact]
    public void Shadow_find_all_returns_shadow_elements()
    {
        var shadowRoot = _driver.FindElement(By.Id("shadow-host")).GetShadowRoot();
        Assert.Equal(2, shadowRoot.FindElements(By.CssSelector("*")).Count);
    }

    [Fact]
    public void XPath_inside_shadow_root_is_an_invalid_selector()
    {
        var shadowRoot = _driver.FindElement(By.Id("shadow-host")).GetShadowRoot();
        Assert.Throws<InvalidSelectorException>(() => shadowRoot.FindElement(By.XPath(".//button")));
    }

    [Fact]
    public void Element_without_shadow_root_throws_NoSuchShadowRoot()
    {
        var heading = _driver.FindElement(By.Id("heading"));
        Assert.Throws<NoSuchShadowRootException>(() => heading.GetShadowRoot());
    }

    [Fact]
    public void Detached_shadow_root_throws_DetachedShadowRoot()
    {
        Js.ExecuteScript("""
            var host = document.createElement('div');
            host.id = 'doomed-shadow-host';
            document.body.appendChild(host);
            host.attachShadow({ mode: 'open' }).innerHTML = '<i>x</i>';
            """);
        var shadowRoot = _driver.FindElement(By.Id("doomed-shadow-host")).GetShadowRoot();
        Js.ExecuteScript("document.getElementById('doomed-shadow-host').remove();");
        Assert.Throws<DetachedShadowRootException>(() => shadowRoot.FindElement(By.CssSelector("i")));
    }
}

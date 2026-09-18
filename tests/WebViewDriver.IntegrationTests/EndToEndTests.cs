using System.Collections.ObjectModel;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace WebViewDriver.IntegrationTests;

/// <summary>
/// The 0.1 milestone gate: standard Selenium API calls running against a real
/// DOM (jsdom) through the full host + bridge + executor stack.
/// </summary>
[Collection("driver")]
public sealed class EndToEndTests
{
    private readonly IWebDriver _driver;
    private IJavaScriptExecutor Js => (IJavaScriptExecutor)_driver;

    public EndToEndTests(DriverFixture fixture) => _driver = fixture.Driver;

    // -- Session & document ---------------------------------------------------

    [Fact]
    public void Title_url_and_page_source_work()
    {
        Assert.Equal("WebViewDriver Fixture", _driver.Title);
        Assert.StartsWith("https://app.local/", _driver.Url);
        Assert.Contains("data-test-id=\"save\"", _driver.PageSource);
    }

    [Fact]
    public void Single_window_model_is_exposed()
    {
        Assert.Equal("main", _driver.CurrentWindowHandle);
        Assert.Equal(new[] { "main" }, _driver.WindowHandles);
    }

    // -- Locators ---------------------------------------------------------------

    [Fact]
    public void Finds_by_css_xpath_id_name_tag_class_and_link_text()
    {
        Assert.Equal("button", _driver.FindElement(By.CssSelector("[data-test-id='save']")).TagName);
        Assert.Equal("WebViewDriver fixture", _driver.FindElement(By.XPath("//h1")).Text);
        Assert.Equal("save", _driver.FindElement(By.Id("save")).GetDomAttribute("id"));
        Assert.Equal("name-input", _driver.FindElement(By.Name("username")).GetDomAttribute("id"));
        Assert.Equal(2, _driver.FindElements(By.TagName("li")).Count);
        Assert.Equal(2, _driver.FindElements(By.ClassName("notice")).Count);
        Assert.Equal("about-link", _driver.FindElement(By.LinkText("About this app")).GetDomAttribute("id"));
        Assert.Equal("about-link", _driver.FindElement(By.PartialLinkText("About")).GetDomAttribute("id"));
    }

    [Fact]
    public void Child_finds_are_scoped_to_the_parent()
    {
        var container = _driver.FindElement(By.Id("container"));
        Assert.Equal("inner text", container.FindElement(By.CssSelector(".inner")).Text);
        Assert.Empty(container.FindElements(By.CssSelector(".notice")));
    }

    [Fact]
    public void Missing_element_throws_NoSuchElement_and_findElements_returns_empty()
    {
        Assert.Throws<NoSuchElementException>(() => _driver.FindElement(By.CssSelector("#definitely-missing")));
        Assert.Empty(_driver.FindElements(By.CssSelector("#definitely-missing")));
    }

    [Fact]
    public void Invalid_selector_throws_InvalidSelectorException()
        => Assert.Throws<InvalidSelectorException>(() => _driver.FindElement(By.CssSelector("[unclosed")));

    // -- Interaction -------------------------------------------------------------

    [Fact]
    public void Click_fires_real_dom_handlers()
    {
        var before = int.Parse(_driver.FindElement(By.Id("click-count")).Text);
        _driver.FindElement(By.CssSelector("[data-test-id='save']")).Click();
        _driver.FindElement(By.CssSelector("[data-test-id='save']")).Click();
        var after = int.Parse(_driver.FindElement(By.Id("click-count")).Text);
        Assert.Equal(before + 2, after);
    }

    [Fact]
    public void SendKeys_updates_value_and_fires_input_and_change_events()
    {
        var input = _driver.FindElement(By.Id("name-input"));
        input.Clear();
        input.SendKeys("Hello WKWebView");

        Assert.Equal("Hello WKWebView", input.GetDomProperty("value"));
        // The echo div only updates via 'input' events — proves framework-safe typing (FR7).
        Assert.Equal("Hello WKWebView", _driver.FindElement(By.Id("typed-echo")).Text);
        Assert.Equal("changed", _driver.FindElement(By.Id("change-log")).Text);

        input.Clear();
        Assert.Equal("", input.GetDomProperty("value"));
    }

    [Fact]
    public void Checkbox_click_toggles_selection()
    {
        var checkbox = _driver.FindElement(By.Id("agree"));
        var initial = checkbox.Selected;
        checkbox.Click();
        Assert.Equal(!initial, checkbox.Selected);
        checkbox.Click();
        Assert.Equal(initial, checkbox.Selected);
    }

    [Fact]
    public void SelectElement_helper_works()
    {
        var select = new SelectElement(_driver.FindElement(By.Id("color")));
        select.SelectByValue("green");
        Assert.Equal("green", select.SelectedOption.GetDomAttribute("value"));
        Assert.Equal("color:green", _driver.FindElement(By.Id("change-log")).Text);
        select.SelectByValue("red");
    }

    // -- Element state -------------------------------------------------------------

    [Fact]
    public void Attribute_property_css_tag_and_enabled_surface_dom_state()
    {
        var input = _driver.FindElement(By.Id("name-input"));
        Assert.Equal("Name", input.GetAttribute("placeholder"));    // Selenium atom through executeScript (FR6)
        Assert.Equal("Name", input.GetDomAttribute("placeholder"));
        Assert.Equal("text", input.GetDomProperty("type"));
        Assert.Equal("input", input.TagName);
        Assert.True(input.Enabled);
        Assert.Equal("none", _driver.FindElement(By.Id("hidden")).GetCssValue("display"));
    }

    [Fact]
    public void Hidden_element_reports_not_displayed()
        => Assert.False(_driver.FindElement(By.Id("hidden")).Displayed);

    [Fact]
    public void Rect_and_window_rect_round_trip()
    {
        // jsdom has no layout: values are zeros, but the command path must work.
        var rect = _driver.FindElement(By.Id("heading")).Location;
        Assert.True(rect.X >= 0 && rect.Y >= 0);
        Assert.True(_driver.Manage().Window.Size.Width >= 0);
    }

    // -- Script execution ------------------------------------------------------------

    [Fact]
    public void ExecuteScript_round_trips_primitives_arrays_objects_and_elements()
    {
        Assert.Equal(2L, Js.ExecuteScript("return 1 + 1;"));
        Assert.Equal(1.5, Js.ExecuteScript("return 1.5;"));
        Assert.Equal("hi", Js.ExecuteScript("return 'hi';"));
        Assert.Equal(true, Js.ExecuteScript("return true;"));
        Assert.Null(Js.ExecuteScript("return null;"));

        var list = Assert.IsType<ReadOnlyCollection<object>>(Js.ExecuteScript("return [1, 'a', [true]];"));
        Assert.Equal(3, list.Count);

        var dictionary = Assert.IsAssignableFrom<IDictionary<string, object>>(Js.ExecuteScript("return { n: 7, s: 'x', nested: { ok: true } };"));
        Assert.Equal(7L, dictionary["n"]);

        var element = Assert.IsAssignableFrom<IWebElement>(Js.ExecuteScript("return document.querySelector('h1');"));
        Assert.Equal("WebViewDriver fixture", element.Text);

        var heading = _driver.FindElement(By.Id("heading"));
        Assert.Equal("heading", Js.ExecuteScript("return arguments[0].id;", heading));
        Assert.Equal("payload-42", Js.ExecuteScript("return arguments[0] + '-' + arguments[1];", "payload", 42));
    }

    [Fact]
    public void ExecuteScript_surfaces_js_exceptions_with_message()
    {
        var ex = Assert.ThrowsAny<WebDriverException>(() => Js.ExecuteScript("throw new Error('kaboom');"));
        Assert.Contains("kaboom", ex.Message);
    }

    [Fact]
    public void ExecuteScript_awaits_returned_promises()
        => Assert.Equal(7L, Js.ExecuteScript("return new Promise(function (resolve) { setTimeout(function () { resolve(7); }, 50); });"));

    [Fact]
    public void ExecuteAsyncScript_resolves_via_callback()
    {
        _driver.Manage().Timeouts().AsynchronousJavaScript = TimeSpan.FromSeconds(5);
        var result = Js.ExecuteAsyncScript(
            "var callback = arguments[arguments.length - 1]; setTimeout(function () { callback(41 + 1); }, 100);");
        Assert.Equal(42L, result);
    }

    // -- Waits -----------------------------------------------------------------------

    [Fact]
    public void WebDriverWait_polls_app_state()
    {
        _driver.FindElement(By.CssSelector("[data-test-id='save']")).Click();
        var ready = new WebDriverWait(_driver, TimeSpan.FromSeconds(10))
            .Until(d => (bool)((IJavaScriptExecutor)d).ExecuteScript("return window.appReady === true && window.delayedFlag === true;")!);
        Assert.True(ready);
    }

    [Fact]
    public void Implicit_wait_applies_to_finds()
    {
        _driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromMilliseconds(400);
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Assert.Throws<NoSuchElementException>(() => _driver.FindElement(By.Id("never-exists")));
            Assert.True(stopwatch.ElapsedMilliseconds >= 350, $"gave up after only {stopwatch.ElapsedMilliseconds} ms");
        }
        finally
        {
            _driver.Manage().Timeouts().ImplicitWait = TimeSpan.Zero;
        }
    }

    // -- Staleness ---------------------------------------------------------------------

    [Fact]
    public void Removed_element_throws_StaleElementReferenceException()
    {
        var removable = _driver.FindElement(By.Id("removable"));
        Assert.Equal("removable", removable.Text);

        _driver.FindElement(By.Id("remove-btn")).Click();

        Assert.Throws<StaleElementReferenceException>(() => removable.Text);
    }

    // -- Screenshots ---------------------------------------------------------------------

    [Fact]
    public void Screenshot_reports_unsupported_for_hosts_without_provider()
    {
        var ex = Assert.ThrowsAny<WebDriverException>(() => ((ITakesScreenshot)_driver).GetScreenshot());
        Assert.Contains("IScreenshotProvider", ex.Message);
    }
}

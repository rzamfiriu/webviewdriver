using OpenQA.Selenium;
using Xunit;

namespace WebViewDriver.IntegrationTests;

[Collection("driver")]
public sealed class InteractabilityAndAtomsTests
{
    private readonly IWebDriver _driver;
    private IJavaScriptExecutor Js => (IJavaScriptExecutor)_driver;

    public InteractabilityAndAtomsTests(DriverFixture fixture) => _driver = fixture.Driver;

    // -- W3C interactability semantics ---------------------------------------------

    [Fact]
    public void Clicking_a_hidden_element_throws_ElementNotInteractable()
    {
        var ex = Assert.Throws<ElementNotInteractableException>(
            () => _driver.FindElement(By.Id("hidden-btn")).Click());
        Assert.Contains("not displayed", ex.Message);
    }

    [Fact]
    public void Typing_into_a_disabled_input_throws_ElementNotInteractable()
    {
        var ex = Assert.Throws<ElementNotInteractableException>(
            () => _driver.FindElement(By.Id("disabled-input")).SendKeys("nope"));
        Assert.Contains("disabled", ex.Message);
    }

    [Fact]
    public void Clear_on_an_already_empty_field_fires_no_events()
    {
        var input = _driver.FindElement(By.Id("name-input"));
        input.Clear(); // may fire events if non-empty; get to a known state
        Js.ExecuteScript("document.getElementById('typed-echo').textContent = 'untouched';");

        input.Clear(); // spec: no-op, so the input listener must not run
        Assert.Equal("untouched", _driver.FindElement(By.Id("typed-echo")).Text);
    }

    [Fact]
    public void Enter_submits_the_owning_form_when_keydown_is_not_canceled()
    {
        var input = _driver.FindElement(By.Id("name-input"));
        input.Clear();
        input.SendKeys("submit me" + Keys.Enter);
        Assert.Equal("submitted", _driver.FindElement(By.Id("change-log")).Text);
    }

    // -- Atoms conformance (FR6): GetAttribute vs GetDomAttribute -------------------

    [Fact]
    public void GetAttribute_absolutizes_href_but_GetDomAttribute_does_not()
    {
        var link = _driver.FindElement(By.Id("about-link"));
        Assert.Equal("#about", link.GetDomAttribute("href"));
        Assert.StartsWith("https://app.local/", link.GetAttribute("href"));
        Assert.EndsWith("#about", link.GetAttribute("href"));
    }

    [Fact]
    public void GetAttribute_reports_boolean_attributes_as_true_or_null()
    {
        var checkbox = _driver.FindElement(By.Id("agree"));
        Js.ExecuteScript("arguments[0].checked = false;", checkbox);
        Assert.Null(checkbox.GetAttribute("checked"));

        Js.ExecuteScript("arguments[0].checked = true;", checkbox);
        Assert.Equal("true", checkbox.GetAttribute("checked"));
        Js.ExecuteScript("arguments[0].checked = false;", checkbox);

        Assert.Equal("true", _driver.FindElement(By.Id("disabled-input")).GetAttribute("disabled"));
    }

    [Fact]
    public void GetAttribute_value_tracks_the_live_value()
    {
        var input = _driver.FindElement(By.Id("name-input"));
        input.Clear();
        input.SendKeys("live");
        Assert.Equal("live", input.GetAttribute("value"));
        Assert.Null(input.GetDomAttribute("value")); // no value *attribute* in markup
        input.Clear();
    }

    // -- Same-document navigation -----------------------------------------------------

    [Fact]
    public void Hash_navigation_completes_and_updates_url()
    {
        var baseUrl = _driver.Url.Split('#')[0];
        _driver.Navigate().GoToUrl(baseUrl + "#about");
        Assert.EndsWith("#about", _driver.Url);

        _driver.Navigate().GoToUrl(baseUrl + "#other");
        Assert.EndsWith("#other", _driver.Url);
    }
}

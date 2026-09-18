using OpenQA.Selenium;
using OpenQA.Selenium.Interactions;
using Xunit;

namespace WebViewDriver.IntegrationTests;

/// <summary>
/// W3C Actions through the default DOM backend, driven by Selenium's real
/// Actions builder against the jsdom fixture.
/// </summary>
[Collection("driver")]
public sealed class ActionsTests : IDisposable
{
    private readonly IWebDriver _driver;
    private IJavaScriptExecutor Js => (IJavaScriptExecutor)_driver;

    public ActionsTests(DriverFixture fixture) => _driver = fixture.Driver;

    // Input state persists across performs by spec; make tests order-independent.
    public void Dispose() => ((IActionExecutor)_driver).ResetInputState();

    [Fact]
    public void Hover_opens_and_leaves_close_a_js_menu()
    {
        var menuButton = _driver.FindElement(By.Id("menu-btn"));
        var panel = _driver.FindElement(By.Id("menu-panel"));

        new OpenQA.Selenium.Interactions.Actions(_driver).MoveToElement(menuButton).Perform();
        Assert.Equal("block", panel.GetCssValue("display"));

        new OpenQA.Selenium.Interactions.Actions(_driver).MoveToElement(_driver.FindElement(By.Id("heading"))).Perform();
        Assert.Equal("none", panel.GetCssValue("display"));
    }

    [Fact]
    public void Drag_and_drop_fires_pointer_sequence()
    {
        var box = _driver.FindElement(By.Id("drag-box"));
        var area = _driver.FindElement(By.Id("drag-area"));

        new OpenQA.Selenium.Interactions.Actions(_driver).DragAndDrop(box, area).Perform();

        Assert.Equal("dropped", _driver.FindElement(By.Id("drag-status")).Text);
    }

    [Fact]
    public void Shift_click_carries_modifier_state()
    {
        var target = _driver.FindElement(By.Id("shift-target"));

        new OpenQA.Selenium.Interactions.Actions(_driver)
            .KeyDown(Keys.Shift)
            .Click(target)
            .KeyUp(Keys.Shift)
            .Perform();
        Assert.Equal("shift:true", _driver.FindElement(By.Id("mod-log")).Text);

        new OpenQA.Selenium.Interactions.Actions(_driver).Click(target).Perform();
        Assert.Equal("shift:false", _driver.FindElement(By.Id("mod-log")).Text);
    }

    [Fact]
    public void Modifier_state_persists_across_performs_until_released()
    {
        var target = _driver.FindElement(By.Id("shift-target"));

        new OpenQA.Selenium.Interactions.Actions(_driver).KeyDown(Keys.Shift).Perform();
        new OpenQA.Selenium.Interactions.Actions(_driver).Click(target).Perform();
        Assert.Equal("shift:true", _driver.FindElement(By.Id("mod-log")).Text);

        ((IActionExecutor)_driver).ResetInputState();
        new OpenQA.Selenium.Interactions.Actions(_driver).Click(target).Perform();
        Assert.Equal("shift:false", _driver.FindElement(By.Id("mod-log")).Text);
    }

    [Fact]
    public void Double_click_synthesizes_dblclick()
    {
        var target = _driver.FindElement(By.Id("dbl-target"));
        var before = int.Parse(_driver.FindElement(By.Id("dbl-count")).Text);

        new OpenQA.Selenium.Interactions.Actions(_driver).DoubleClick(target).Perform();

        Assert.Equal(before + 1, int.Parse(_driver.FindElement(By.Id("dbl-count")).Text));
    }

    [Fact]
    public void Typing_through_actions_updates_controlled_inputs()
    {
        var input = _driver.FindElement(By.Id("name-input"));
        input.Clear();

        new OpenQA.Selenium.Interactions.Actions(_driver).Click(input).SendKeys("via actions").Perform();

        Assert.Equal("via actions", input.GetDomProperty("value"));
        Assert.Equal("via actions", _driver.FindElement(By.Id("typed-echo")).Text);
        input.Clear();
    }

    [Fact]
    public void Wheel_scroll_dispatches_wheel_events()
    {
        var wheelBox = _driver.FindElement(By.Id("wheel-box"));
        var origin = new WheelInputDevice.ScrollOrigin { Element = wheelBox };

        new OpenQA.Selenium.Interactions.Actions(_driver).ScrollFromOrigin(origin, 0, 120).Perform();

        Assert.Equal("wheel:down", _driver.FindElement(By.Id("wheel-log")).Text);
    }

    [Fact]
    public void Pauses_are_honored()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        new OpenQA.Selenium.Interactions.Actions(_driver)
            .Pause(TimeSpan.FromMilliseconds(300))
            .Perform();
        Assert.True(stopwatch.ElapsedMilliseconds >= 250, $"perform returned after only {stopwatch.ElapsedMilliseconds} ms");
    }
}

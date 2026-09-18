// Smoke test against the real Mac Catalyst sample app + WKWebView.
// Usage: SmokeTest <port> <token>
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using WebViewDriver.Selenium;

var port = int.Parse(args[0]);
var token = args[1];

using var driver = WebViewDriverClient.Connect(port, token, target: "main", timeout: TimeSpan.FromSeconds(60));
var js = (IJavaScriptExecutor)driver;

void Check(string name, Func<bool> assertion)
{
    var ok = false;
    string? detail = null;
    try
    {
        ok = assertion();
    }
    catch (Exception ex)
    {
        detail = ex.Message.Split('\n')[0];
    }

    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail is null ? "" : "  -- " + detail)}");
    if (!ok)
    {
        Environment.ExitCode = 1;
    }
}

Check("title", () => driver.Title == "WebViewDriver Fixture");
Check("find css", () => driver.FindElement(By.CssSelector("[data-test-id='save']")).TagName == "button");
Check("find xpath text", () => driver.FindElement(By.XPath("//h1")).Text == "WebViewDriver fixture");
Check("click fires handlers", () =>
{
    var before = int.Parse(driver.FindElement(By.Id("click-count")).Text);
    driver.FindElement(By.CssSelector("[data-test-id='save']")).Click();
    return int.Parse(driver.FindElement(By.Id("click-count")).Text) == before + 1;
});
Check("sendKeys + input events", () =>
{
    var input = driver.FindElement(By.Id("name-input"));
    input.Clear();
    input.SendKeys("Real WKWebView");
    return driver.FindElement(By.Id("typed-echo")).Text == "Real WKWebView";
});
Check("GetAttribute atom", () => driver.FindElement(By.Id("name-input")).GetAttribute("placeholder") == "Name");
Check("displayed: hidden=false, save=true", () =>
    !driver.FindElement(By.Id("hidden")).Displayed && driver.FindElement(By.Id("save")).Displayed);
Check("rect has real layout", () =>
{
    var size = driver.FindElement(By.Id("save")).Size;
    return size.Width > 0 && size.Height > 0;
});
Check("executeScript promise", () => (long)js.ExecuteScript("return Promise.resolve(6*7);")! == 42L);
Check("WebDriverWait", () =>
    new WebDriverWait(driver, TimeSpan.FromSeconds(5)).Until(d => (bool)((IJavaScriptExecutor)d).ExecuteScript("return window.appReady === true;")!));
Check("SelectElement", () =>
{
    var select = new SelectElement(driver.FindElement(By.Id("color")));
    select.SelectByValue("green");
    return select.SelectedOption.GetDomAttribute("value") == "green";
});
Check("stale element detection", () =>
{
    var removable = driver.FindElement(By.Id("removable"));
    driver.FindElement(By.Id("remove-btn")).Click();
    try
    {
        _ = removable.Text;
        return false;
    }
    catch (StaleElementReferenceException)
    {
        return true;
    }
});
Check("page screenshot via WKWebView.TakeSnapshot", () =>
{
    var screenshot = ((ITakesScreenshot)driver).GetScreenshot();
    File.WriteAllBytes("/tmp/webviewdriver-smoke.png", screenshot.AsByteArray);
    return screenshot.AsByteArray.Length > 1000;
});

// -- 0.2 surface: frames, shadow DOM, interactability, element shots, navigation --

Check("frame: switch by index, find, click, back to top", () =>
{
    driver.SwitchTo().Frame(0);
    var text = driver.FindElement(By.Id("frame-text")).Text;
    driver.FindElement(By.Id("frame-btn")).Click();
    var clicked = driver.FindElement(By.Id("frame-text")).Text;
    driver.SwitchTo().DefaultContent();
    return text == "inside frame" && clicked == "frame clicked"
        && driver.FindElements(By.Id("heading")).Count == 1;
});
Check("frame: switch by element", () =>
{
    driver.SwitchTo().Frame(driver.FindElement(By.Id("frame1")));
    var found = driver.FindElements(By.Id("frame-btn")).Count == 1;
    driver.SwitchTo().DefaultContent();
    return found;
});
Check("shadow DOM: GetShadowRoot + find + click", () =>
{
    var shadowRoot = driver.FindElement(By.Id("shadow-host")).GetShadowRoot();
    shadowRoot.FindElement(By.CssSelector("#shadow-btn")).Click();
    return shadowRoot.FindElement(By.CssSelector(".shadow-label")).Text == "shadow clicked";
});
Check("interactability: hidden click throws", () =>
{
    try { driver.FindElement(By.Id("hidden-btn")).Click(); return false; }
    catch (ElementNotInteractableException) { return true; }
});
Check("interactability: disabled sendKeys throws", () =>
{
    try { driver.FindElement(By.Id("disabled-input")).SendKeys("x"); return false; }
    catch (ElementNotInteractableException) { return true; }
});
Check("interactability: overlay intercepts click (real hit-testing)", () =>
{
    try { driver.FindElement(By.Id("covered-btn")).Click(); return false; }
    catch (ElementClickInterceptedException ex) { return ex.Message.Contains("overlay"); }
});
Check("element screenshot via WKSnapshotConfiguration.Rect", () =>
{
    var page = ((ITakesScreenshot)driver).GetScreenshot().AsByteArray;
    var element = ((ITakesScreenshot)driver.FindElement(By.Id("save"))).GetScreenshot().AsByteArray;
    File.WriteAllBytes("/tmp/webviewdriver-element.png", element);
    return element.Length > 100 && element.Length < page.Length;
});
Check("navigation: page2, back, forward", () =>
{
    var indexUrl = driver.Url;
    driver.FindElement(By.Id("page2-link")).Click();
    new WebDriverWait(driver, TimeSpan.FromSeconds(10)).Until(d => d.Title == "Page Two");
    driver.Navigate().Back();
    new WebDriverWait(driver, TimeSpan.FromSeconds(10)).Until(d => d.Title == "WebViewDriver Fixture");
    driver.Navigate().Forward();
    new WebDriverWait(driver, TimeSpan.FromSeconds(10)).Until(d => d.Title == "Page Two");
    driver.Navigate().GoToUrl(indexUrl);
    return driver.Title == "WebViewDriver Fixture";
});
Check("navigation: refresh produces a fresh document", () =>
{
    ((IJavaScriptExecutor)driver).ExecuteScript("window.__smokeMarker = true;");
    driver.Navigate().Refresh();
    return ((IJavaScriptExecutor)driver).ExecuteScript("return window.__smokeMarker === undefined;") is true;
});

// -- 0.3 surface: W3C Actions through the default DOM backend --

Check("actions: hover opens JS menu at real coordinates", () =>
{
    new OpenQA.Selenium.Interactions.Actions(driver).MoveToElement(driver.FindElement(By.Id("menu-btn"))).Perform();
    var open = driver.FindElement(By.Id("menu-panel")).GetCssValue("display") == "block";
    new OpenQA.Selenium.Interactions.Actions(driver).MoveToElement(driver.FindElement(By.Id("heading"))).Perform();
    var closed = driver.FindElement(By.Id("menu-panel")).GetCssValue("display") == "none";
    return open && closed;
});
Check("actions: drag moves the box (real layout)", () =>
{
    var box = driver.FindElement(By.Id("drag-box"));
    var leftBefore = box.GetCssValue("left");
    new OpenQA.Selenium.Interactions.Actions(driver).DragAndDrop(box, driver.FindElement(By.Id("drag-area"))).Perform();
    return driver.FindElement(By.Id("drag-status")).Text == "dropped"
        && box.GetCssValue("left") != leftBefore;
});
Check("actions: shift-click carries modifier, reset clears it", () =>
{
    var target = driver.FindElement(By.Id("shift-target"));
    new OpenQA.Selenium.Interactions.Actions(driver).KeyDown(Keys.Shift).Click(target).KeyUp(Keys.Shift).Perform();
    var withShift = driver.FindElement(By.Id("mod-log")).Text == "shift:true";
    ((OpenQA.Selenium.IActionExecutor)driver).ResetInputState();
    new OpenQA.Selenium.Interactions.Actions(driver).Click(target).Perform();
    return withShift && driver.FindElement(By.Id("mod-log")).Text == "shift:false";
});
Check("actions: double-click", () =>
{
    var before = int.Parse(driver.FindElement(By.Id("dbl-count")).Text);
    new OpenQA.Selenium.Interactions.Actions(driver).DoubleClick(driver.FindElement(By.Id("dbl-target"))).Perform();
    return int.Parse(driver.FindElement(By.Id("dbl-count")).Text) == before + 1;
});
Check("actions: wheel scroll", () =>
{
    var origin = new OpenQA.Selenium.Interactions.WheelInputDevice.ScrollOrigin
    {
        Element = driver.FindElement(By.Id("wheel-box")),
    };
    new OpenQA.Selenium.Interactions.Actions(driver).ScrollFromOrigin(origin, 0, 120).Perform();
    return driver.FindElement(By.Id("wheel-log")).Text == "wheel:down";
});
Check("actions: typing via key events", () =>
{
    var input = driver.FindElement(By.Id("name-input"));
    input.Clear();
    new OpenQA.Selenium.Interactions.Actions(driver).Click(input).SendKeys("Act10ns").Perform();
    return driver.FindElement(By.Id("typed-echo")).Text == "Act10ns";
});
Check("multi-target: ListTargets reports main", () =>
{
    var targets = WebViewDriverClient.ListTargets(port, token);
    return targets.Any(t => t.Name == "main" && t.Screenshots);
});

Console.WriteLine(Environment.ExitCode == 0 ? "SMOKE TEST PASSED" : "SMOKE TEST FAILED");

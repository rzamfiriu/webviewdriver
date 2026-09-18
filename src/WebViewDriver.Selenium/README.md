# WebViewDriver.Selenium

This is the test-side package for
[WebViewDriver](https://github.com/rzamfiriu/webviewdriver). It provides a
Selenium `RemoteWebDriver` for a webview exposed by `WebViewDriver.Host`.

Install `WebViewDriver.Host` in the app under test before using this package.

## Connect

Start the app with a port and random token:

```csharp
var port = GetFreePort();
var token = Guid.NewGuid().ToString("N");

using var app = Process.Start(new ProcessStartInfo(appPath)
{
    Environment =
    {
        ["WEBVIEWDRIVER_PORT"] = port.ToString(),
        ["WEBVIEWDRIVER_TOKEN"] = token,
    },
});
```

Connect to the named webview:

```csharp
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using WebViewDriver.Selenium;

using IWebDriver driver = WebViewDriverClient.Connect(
    port,
    token,
    target: "main",
    timeout: TimeSpan.FromSeconds(30));

driver.FindElement(By.CssSelector("[data-test-id='save']")).Click();

new WebDriverWait(driver, TimeSpan.FromSeconds(10))
    .Until(d => (bool)((IJavaScriptExecutor)d)
        .ExecuteScript("return window.appReady"));
```

The returned object is a standard Selenium `RemoteWebDriver`, so existing
locators, waits, page objects, `SelectElement`, script execution, and Actions
can use it directly.

## Readiness

Tests can wait for a DOM condition while connecting:

```csharp
var options = new WebViewDriverClientOptions
{
    Timeout = TimeSpan.FromSeconds(30),
    ReadinessScript = "return window.appReady === true",
};

using var driver = WebViewDriverClient.Connect(
    port,
    token,
    "main",
    options);
```

Use `WebViewDriverClient.ListTargets(port, token)` to list webviews currently
attached by the app.

## Actions

The default Actions backend dispatches DOM pointer, keyboard, and wheel events.
Use `WebViewDriver.Selenium.AppiumMac` when a test requires trusted operating
system input.

## Requirements and limitations

- Targets .NET 10.
- Supports Selenium.WebDriver 4.30.0 and later.
- Frames must be same-origin.
- Only one session should control a given webview at a time.
- Cookies, alerts, BiDi, CDP emulation, and network interception are not
  implemented.

See the
[project documentation](https://github.com/rzamfiriu/webviewdriver#readme)
for the full command list and security notes.

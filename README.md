# WebViewDriver

WebViewDriver lets Selenium tests control a `WKWebView` embedded in a .NET app.
It is intended for MAUI `WebView`, `HybridWebView`, `BlazorWebView`, and other
Mac Catalyst or iOS apps that own their webview.

The current release is **0.3.0**.
The packages target .NET 10.

## Packages

- `WebViewDriver.Host` goes in the app under test. It exposes an opt-in,
  loopback-only JavaScript evaluation endpoint.
- `WebViewDriver.Selenium` goes in the test project. It implements Selenium's
  `ICommandExecutor` and returns a normal `RemoteWebDriver`.
- `WebViewDriver.Selenium.AppiumMac` is optional. It uses an Appium mac2
  session when tests need real mouse and keyboard input.

WebViewDriver does not replace EdgeDriver on Windows or Appium on Android.

## Basic use

Register the host while building the app. Keep this code out of production
builds.

```csharp
// MauiProgram.cs
#if DEBUG
WebViewDriverHost.TryStartFromEnvironment();

HybridWebViewHandler.Mapper.AppendToMapping("WebViewDriver", (handler, _) =>
{
#if MACCATALYST || IOS
    WebViewDriverHost.Attach("main", (WebKit.WKWebView)handler.PlatformView);
#endif
});
#endif
```

For a custom webview wrapper, attach an evaluation delegate instead:

```csharp
WebViewDriverHost.Attach(
    "main",
    script => myWebView.EvaluateJavaScriptAsync(script));
```

Start the app with a port and a random token, then connect from the test:

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

The returned object is a Selenium `RemoteWebDriver`. Existing page objects,
waits, locators, and `SelectElement` code can use it directly.

The sample app is in `samples/WebViewDriver.Sample`. `tools/SmokeTest` shows
how the test side is set up.

## Supported commands

Version 0.3 supports:

- navigation, URL, title, and page source
- CSS and XPath locators, plus Selenium's id, name, class, tag, and link
  locators
- child searches, open shadow roots, and same-origin frames
- click, clear, send keys, element state, attributes, properties, CSS values,
  and rectangles
- synchronous and asynchronous JavaScript, including element references in
  arguments and return values
- implicit waits and Selenium's JavaScript atoms
- page and element screenshots
- W3C Actions for pointer, keyboard, pause, and wheel sources
- named webview targets and readiness checks

`Selenium.WebDriver` 4.30.0 and later are supported. CI tests the oldest
supported version, an intermediate version, and the current version.

## Actions

By default, Actions dispatch DOM events inside the page. This covers JavaScript
hover handlers, pointer-event drags, modifier clicks, double-clicks, typing,
and wheel events.

Those events are synthetic. They cannot activate CSS `:hover`, satisfy
`Event.isTrusted`, or reliably drive every canvas and HTML drag-and-drop
implementation.

Use the Appium mac2 backend when real input is required:

```csharp
using var driver = WebViewDriverClient.Connect(
    port,
    token,
    "main",
    new WebViewDriverClientOptions
    {
        ActionsBackend = Mac2ActionsBackend.ForDriver(mac2Driver),
    });
```

The mac2 driver controls the native app and sends trusted input. WebViewDriver
continues to handle DOM queries and script execution.

## Readiness and multiple webviews

The host can publish app-level readiness:

```csharp
WebViewDriverHost.SetReady("main", false);
// Finish application startup.
WebViewDriverHost.SetReady("main", true);
```

Tests can also wait for a DOM condition while connecting:

```csharp
var options = new WebViewDriverClientOptions
{
    ReadinessScript = "return window.appReady === true",
};
```

Use `WebViewDriverClient.ListTargets(port, token)` to inspect attached
webviews. Each target has its own element and frame state.

## Security

The host provides JavaScript evaluation, so it must only be enabled in test
builds.

- It listens on `127.0.0.1` only.
- It does not start unless both `WEBVIEWDRIVER_PORT` and
  `WEBVIEWDRIVER_TOKEN` are set.
- Every request must include the token.
- Requests are size-limited and evaluations are serialized per target.
- The endpoint has no shell, file-system, or reflection API.

Keep the registration code behind `#if DEBUG` or an equivalent QA build
condition. The host logs a warning whenever the endpoint starts.

## Limitations

- Frames must be same-origin.
- Only one session should control a given webview at a time.
- Cookies, alerts, BiDi, CDP emulation, network interception, multi-touch, and
  pen input are not implemented.
- Synthetic Actions have the input limitations described above.
- Windows WebView2 should use EdgeDriver. Android webviews should use
  chromedriver or Appium contexts. Appium XCUITest already supports many iOS
  webview scenarios and may be a better fit when the full native app must be
  automated.

## Build and test

```bash
dotnet test tests/WebViewDriver.Tests
dotnet test tests/WebViewDriver.IntegrationTests
dotnet test tests/WebViewDriver.AppiumMac.Tests

dotnet build samples/WebViewDriver.Sample \
    -f net10.0-maccatalyst
```

The integration tests require Node.js for the jsdom fixture. Building the
sample requires the MAUI workload. If the installed Xcode version is newer
than the version expected by the .NET workload, pass
`-p:ValidateXcodeVersion=false`.

## License

MIT

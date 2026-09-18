# WebViewDriver.Selenium.AppiumMac

This optional package provides trusted mouse and keyboard input for
[WebViewDriver](https://github.com/rzamfiriu/webviewdriver) through an Appium
mac2 session.

The default backend in `WebViewDriver.Selenium` dispatches synthetic DOM
events. That is sufficient for most controls, but it cannot activate CSS
`:hover`, satisfy `Event.isTrusted`, or reliably drive every canvas and
HTML drag-and-drop implementation.

## Prerequisites

Install Appium and the mac2 driver:

```bash
npm install --global appium
appium driver install mac2
```

Create and start the mac2 session for the application before connecting
WebViewDriver. The mac2 session requires macOS Accessibility permission.

## Setup

Pass the existing Appium driver to the WebViewDriver client:

```csharp
using WebViewDriver.Selenium;
using WebViewDriver.Selenium.AppiumMac;

var options = new WebViewDriverClientOptions
{
    ActionsBackend = Mac2ActionsBackend.ForDriver(mac2Driver),
};

using var driver = WebViewDriverClient.Connect(
    port,
    token,
    target: "main",
    options);
```

Selenium Actions submitted through `driver` now use the mac2 session:

```csharp
new OpenQA.Selenium.Interactions.Actions(driver)
    .MoveToElement(canvas)
    .ClickAndHold()
    .MoveByOffset(100, 40)
    .Release()
    .Perform();
```

WebViewDriver still handles DOM queries and script execution. The mac2 session
only handles Actions.

## Coordinate mapping

The backend finds `XCUIElementTypeWebView` in the mac2 accessibility tree and
maps CSS viewport coordinates into its screen rectangle. Page zoom is handled
using the ratio between the webview width and `window.innerWidth`.

If the application has more than one accessible webview, implement
`IMac2Session` to select the correct accessibility element and pass that
implementation to `Mac2ActionsBackend`.

See the
[project documentation](https://github.com/rzamfiriu/webviewdriver#readme)
for the complete WebViewDriver setup.

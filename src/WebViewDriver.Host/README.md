# WebViewDriver.Host

This is the app-side package for
[WebViewDriver](https://github.com/rzamfiriu/webviewdriver). It exposes an
embedded webview to tests through a local JavaScript evaluation endpoint.

Install this package in the app under test. Install
`WebViewDriver.Selenium` in the test project.

## Setup

Register the host and attach the webview after its platform view is created:

```csharp
using Microsoft.Maui.Handlers;
using WebViewDriver.Host;

#if DEBUG
WebViewDriverHost.TryStartFromEnvironment();

HybridWebViewHandler.Mapper.AppendToMapping("WebViewDriver", (handler, _) =>
{
#if MACCATALYST || IOS
    WebViewDriverHost.Attach(
        "main",
        (WebKit.WKWebView)handler.PlatformView);
#endif
});
#endif
```

Custom webview wrappers can provide an evaluation delegate:

```csharp
WebViewDriverHost.Attach(
    "main",
    script => myWebView.EvaluateJavaScriptAsync(script));
```

The test launcher must set both environment variables:

```text
WEBVIEWDRIVER_PORT=17321
WEBVIEWDRIVER_TOKEN=<random per-run token>
```

The host remains inactive when either value is missing.

## Readiness

An app can prevent clients from connecting before startup is complete:

```csharp
WebViewDriverHost.SetReady("main", false);
// Finish application startup.
WebViewDriverHost.SetReady("main", true);
```

## Security

This package provides JavaScript evaluation inside the app. Include and
register it only in Debug or QA builds.

- The endpoint listens on `127.0.0.1` only.
- Every request requires the session token.
- Requests are size-limited.
- Evaluations are serialized per webview.
- No shell, file-system, or reflection API is exposed.

See the
[project documentation](https://github.com/rzamfiriu/webviewdriver#readme)
for the complete setup and supported commands.

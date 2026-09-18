#if IOS || MACCATALYST
using WebKit;

namespace WebViewDriver.Host;

public static partial class WebViewDriverHost
{
    /// <summary>
    /// Registers a <see cref="WKWebView"/> under a name the test side can address,
    /// with screenshot support via WKWebView.TakeSnapshot.
    /// </summary>
    public static void Attach(string name, WKWebView webView)
        => Attach(name, new WKWebViewScriptHost(webView));
}
#endif

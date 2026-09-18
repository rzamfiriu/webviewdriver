#if IOS || MACCATALYST
using Foundation;
using WebKit;

namespace WebViewDriver.Host;

/// <summary>
/// Adapts a <see cref="WKWebView"/> to <see cref="IScriptHost"/> (JS eval on the
/// main thread) and <see cref="IScreenshotProvider"/> (via WKWebView.TakeSnapshot).
/// </summary>
public sealed class WKWebViewScriptHost : IScriptHost, IScreenshotProvider
{
    private readonly WKWebView _webView;

    public WKWebViewScriptHost(WKWebView webView)
        => _webView = webView ?? throw new ArgumentNullException(nameof(webView));

    public Task<string?> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _webView.InvokeOnMainThread(() =>
        {
            _webView.EvaluateJavaScript(new NSString(script), (result, error) =>
            {
                if (error is not null)
                {
                    completion.TrySetException(new InvalidOperationException(
                        $"WKWebView JavaScript evaluation failed: {error.LocalizedDescription}"));
                }
                else
                {
                    completion.TrySetResult(result?.ToString());
                }
            });
        });
        return completion.Task;
    }

    public Task<byte[]> TakePngScreenshotAsync(ScreenshotRegion? region, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _webView.InvokeOnMainThread(() =>
        {
            var configuration = new WKSnapshotConfiguration();
            if (region is { } r)
            {
                // WKSnapshotConfiguration.Rect is in view coordinates (CSS points),
                // so WebKit does the cropping — no client-side Retina pixel math.
                configuration.Rect = new CoreGraphics.CGRect(r.X, r.Y, r.Width, r.Height);
            }

            _webView.TakeSnapshot(configuration, (image, error) =>
            {
                if (error is not null || image is null)
                {
                    completion.TrySetException(new InvalidOperationException(
                        $"WKWebView snapshot failed: {error?.LocalizedDescription ?? "no image returned"}"));
                    return;
                }

                using var png = image.AsPNG();
                completion.TrySetResult(png?.ToArray() ?? Array.Empty<byte>());
            });
        });
        return completion.Task;
    }
}
#endif

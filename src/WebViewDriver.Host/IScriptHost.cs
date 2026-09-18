namespace WebViewDriver.Host;

/// <summary>
/// Abstraction over a webview (or anything else) that can evaluate a JavaScript
/// expression and return its completion value as a string.
/// </summary>
/// <remarks>
/// The scripts sent by the WebViewDriver.Selenium client always terminate in a
/// <c>JSON.stringify(...)</c> expression, so implementations only need to return the
/// resulting string verbatim (e.g. <c>WKWebView.EvaluateJavaScriptAsync</c> output).
/// </remarks>
public interface IScriptHost
{
    Task<string?> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken);
}

/// <summary>A viewport-relative region in CSS points (WKWebView view coordinates).</summary>
public readonly record struct ScreenshotRegion(double X, double Y, double Width, double Height);

/// <summary>
/// Optionally implemented by an <see cref="IScriptHost"/> that can produce a PNG
/// screenshot of the webview (e.g. via <c>WKWebView.TakeSnapshot</c>).
/// </summary>
public interface IScreenshotProvider
{
    /// <param name="region">Viewport region to capture, or null for the full webview.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<byte[]> TakePngScreenshotAsync(ScreenshotRegion? region, CancellationToken cancellationToken);
}

/// <summary>Wraps a delegate as an <see cref="IScriptHost"/>.</summary>
public sealed class DelegateScriptHost : IScriptHost
{
    private readonly Func<string, Task<string?>> _evaluate;

    public DelegateScriptHost(Func<string, Task<string?>> evaluate)
        => _evaluate = evaluate ?? throw new ArgumentNullException(nameof(evaluate));

    public Task<string?> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken)
        => _evaluate(script);
}

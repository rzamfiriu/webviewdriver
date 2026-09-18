using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Interactions;

namespace WebViewDriver.Selenium.AppiumMac;

/// <summary>A rectangle in Mac screen points.</summary>
public readonly record struct ScreenRect(double X, double Y, double Width, double Height);

/// <summary>
/// The slice of an Appium mac2 session the backend needs. Abstracted so the
/// coordinate mapping and sequence translation are unit-testable without an
/// Appium server.
/// </summary>
public interface IMac2Session
{
    /// <summary>
    /// The screen frame of the app's webview area, from the accessibility tree
    /// (XCUIElementTypeWebView) — mac2 reports frames in screen points, which is
    /// what makes host-side geometry APIs unnecessary.
    /// </summary>
    ScreenRect GetWebViewScreenRect();

    void PerformActions(IList<ActionSequence> sequences);

    void ReleaseActions();
}

/// <summary>Default implementation over a live Appium mac2 driver.</summary>
public sealed class AppiumMac2Session : IMac2Session
{
    private readonly AppiumDriver _driver;

    public AppiumMac2Session(AppiumDriver driver)
        => _driver = driver ?? throw new ArgumentNullException(nameof(driver));

    public ScreenRect GetWebViewScreenRect()
    {
        var webView = _driver.FindElement(MobileBy.ClassName("XCUIElementTypeWebView"));
        var location = webView.Location;
        var size = webView.Size;
        return new ScreenRect(location.X, location.Y, size.Width, size.Height);
    }

    public void PerformActions(IList<ActionSequence> sequences)
        => _driver.PerformActions(sequences);

    public void ReleaseActions()
        => _driver.ResetInputState();
}

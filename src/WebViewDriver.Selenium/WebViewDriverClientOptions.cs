using OpenQA.Selenium;
using WebViewDriver.Selenium.Actions;

namespace WebViewDriver.Selenium;

/// <summary>Optional connection settings and diagnostics hooks (FR10).</summary>
public sealed class WebViewDriverClientOptions
{
    /// <summary>
    /// Executes W3C Actions (FR8). Defaults to <see cref="DomActionsBackend"/>
    /// (synthetic DOM events); plug in WebViewDriver.Selenium.AppiumMac's
    /// Mac2ActionsBackend for trusted OS input.
    /// </summary>
    public IActionsBackend? ActionsBackend { get; set; }

    /// <summary>
    /// Optional readiness predicate (FR9): a JavaScript function body returning
    /// a boolean, e.g. <c>"return window.appReady === true"</c>. Connect polls
    /// it (after the document is interactive) until true or timeout.
    /// </summary>
    public string? ReadinessScript { get; set; }

    /// <summary>How long to wait for the endpoint and target; default 30 s.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Navigation wait policy: <see cref="PageLoadStrategy.Normal"/> waits for
    /// readyState 'complete', <see cref="PageLoadStrategy.Eager"/> accepts
    /// 'interactive'. <see cref="PageLoadStrategy.None"/> returns immediately
    /// after the navigation is issued.
    /// </summary>
    public PageLoadStrategy PageLoadStrategy { get; set; } = PageLoadStrategy.Normal;

    /// <summary>Per-command log sink: command name, duration, outcome.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// When set, every request/response on the wire is appended to this file as
    /// JSON lines — attach it to bug reports.
    /// </summary>
    public string? WireCaptureFile { get; set; }
}

using System.Text.Json.Nodes;

namespace WebViewDriver.Selenium.Actions;

/// <summary>
/// Pluggable executor for W3C Actions (FR8). The default
/// <see cref="DomActionsBackend"/> synthesizes DOM events inside the page;
/// alternative backends (e.g. WebViewDriver.Selenium.AppiumMac's mac2 backend)
/// can deliver trusted OS input instead.
/// </summary>
public interface IActionsBackend
{
    /// <param name="sequences">
    /// The raw W3C action sequences from the Selenium client. Element origins
    /// appear as W3C element-reference dictionaries
    /// (<c>element-6066-11e4-a52e-4f735466cecf</c>).
    /// </param>
    /// <param name="context">Access to the page for coordinate resolution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PerformAsync(JsonArray sequences, IActionsContext context, CancellationToken cancellationToken);

    /// <summary>W3C Release Actions: undo all pressed inputs and clear state.</summary>
    Task ReleaseAsync(IActionsContext context, CancellationToken cancellationToken);
}

/// <summary>
/// What a backend may ask of the page. Scripts run through the in-app eval
/// bridge; arguments may contain W3C element-reference dictionaries, which are
/// resolved to live elements before the script runs.
/// </summary>
public interface IActionsContext
{
    /// <param name="fnBody">JavaScript function body (receives <c>arguments</c>).</param>
    /// <param name="args">JSON array of arguments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The script's JSON result.</returns>
    /// <exception cref="OpenQA.Selenium.WebDriverException">Typed per the bridge error taxonomy (stale element etc.).</exception>
    Task<JsonNode?> ExecuteScriptAsync(string fnBody, JsonArray args, CancellationToken cancellationToken);
}

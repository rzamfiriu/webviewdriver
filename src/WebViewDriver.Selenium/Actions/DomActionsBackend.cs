using System.Text.Json.Nodes;

namespace WebViewDriver.Selenium.Actions;

/// <summary>
/// Default Actions backend: synthesizes pointer/mouse/keyboard/wheel event
/// sequences inside the page. Covers JS-driven hover, drag (pointer-event
/// based), modifier clicks, double clicks, typing and wheel scrolling. Events
/// are not trusted OS input: CSS :hover, HTML5 drag-and-drop and isTrusted
/// checks need a trusted backend such as the Appium mac2 adapter.
/// </summary>
public sealed class DomActionsBackend : IActionsBackend
{
    public Task PerformAsync(JsonArray sequences, IActionsContext context, CancellationToken cancellationToken)
    {
        KeyMapping.AnnotateKeyActions(sequences);
        return context.ExecuteScriptAsync(ActionsScripts.Perform, new JsonArray(sequences.DeepClone()), cancellationToken);
    }

    public Task ReleaseAsync(IActionsContext context, CancellationToken cancellationToken)
        => context.ExecuteScriptAsync(ActionsScripts.Release, new JsonArray(), cancellationToken);
}

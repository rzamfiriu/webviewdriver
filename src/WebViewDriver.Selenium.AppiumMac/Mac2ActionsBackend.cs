using System.Text.Json.Nodes;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Interactions;
using WebViewDriver.Selenium.Actions;

namespace WebViewDriver.Selenium.AppiumMac;

/// <summary>
/// Executes W3C Actions as trusted OS input through an Appium mac2 session
/// (FR8). DOM coordinates are mapped to screen points using the webview's
/// accessibility frame and the page's innerWidth:
/// <c>screen = webviewFrame.origin + css * (webviewFrame.width / innerWidth)</c>
/// (CSS px and Mac points are 1:1 at default zoom; the ratio corrects for page
/// zoom — Retina scaling never enters the equation because both sides are in
/// points).
/// </summary>
public sealed class Mac2ActionsBackend : IActionsBackend
{
    private const string ElementKey = "element-6066-11e4-a52e-4f735466cecf";

    private const string ViewportRectScript = """
        var el = arguments[0];
        if (el.scrollIntoView) {
          try { el.scrollIntoView({ block: 'center', inline: 'center' }); } catch (e) {}
        }
        var r = el.getBoundingClientRect();
        return { x: r.left, y: r.top, width: r.width, height: r.height };
        """;

    private const string ViewportSizeScript = "return { width: window.innerWidth, height: window.innerHeight };";

    private readonly IMac2Session _session;

    public Mac2ActionsBackend(IMac2Session session)
        => _session = session ?? throw new ArgumentNullException(nameof(session));

    /// <summary>Convenience for the common case: wrap a live Appium mac2 driver.</summary>
    public static Mac2ActionsBackend ForDriver(AppiumDriver mac2Driver)
        => new(new AppiumMac2Session(mac2Driver));

    public async Task PerformAsync(JsonArray sequences, IActionsContext context, CancellationToken cancellationToken)
    {
        var webView = _session.GetWebViewScreenRect();
        var viewport = await context.ExecuteScriptAsync(ViewportSizeScript, new JsonArray(), cancellationToken).ConfigureAwait(false);
        var innerWidth = (double?)viewport?["width"] ?? webView.Width;
        var scale = innerWidth > 0 ? webView.Width / innerWidth : 1.0;

        var mapper = new CoordinateMapper(webView, scale);
        var translated = new List<ActionSequence>();
        foreach (var sequenceNode in sequences)
        {
            if (sequenceNode is not JsonObject sequence)
            {
                continue;
            }

            var actionSequence = await TranslateSequenceAsync(sequence, mapper, context, cancellationToken).ConfigureAwait(false);
            if (actionSequence is not null)
            {
                translated.Add(actionSequence);
            }
        }

        if (translated.Count > 0)
        {
            _session.PerformActions(translated);
        }
    }

    public Task ReleaseAsync(IActionsContext context, CancellationToken cancellationToken)
    {
        _session.ReleaseActions();
        return Task.CompletedTask;
    }

    private sealed record CoordinateMapper(ScreenRect WebView, double Scale)
    {
        public (int X, int Y) ToScreen(double cssX, double cssY)
            => ((int)Math.Round(WebView.X + cssX * Scale), (int)Math.Round(WebView.Y + cssY * Scale));

        public int ToScreenDelta(double cssDelta) => (int)Math.Round(cssDelta * Scale);
    }

    private async Task<ActionSequence?> TranslateSequenceAsync(JsonObject sequence, CoordinateMapper mapper, IActionsContext context, CancellationToken ct)
    {
        var type = (string?)sequence["type"];
        var id = (string?)sequence["id"] ?? Guid.NewGuid().ToString();
        if (sequence["actions"] is not JsonArray actions)
        {
            return null;
        }

        switch (type)
        {
            case "pointer":
            {
                var device = new PointerInputDevice(PointerKind.Mouse, id);
                var result = new ActionSequence(device);
                foreach (var node in actions)
                {
                    if (node is not JsonObject action)
                    {
                        continue;
                    }

                    switch ((string?)action["type"])
                    {
                        case "pause":
                            result.AddAction(device.CreatePause(DurationOf(action)));
                            break;
                        case "pointerMove":
                            result.AddAction(await TranslateMoveAsync(action, device, mapper, context, ct).ConfigureAwait(false));
                            break;
                        case "pointerDown":
                            result.AddAction(device.CreatePointerDown(ButtonOf(action)));
                            break;
                        case "pointerUp":
                            result.AddAction(device.CreatePointerUp(ButtonOf(action)));
                            break;
                    }
                }

                return result;
            }

            case "key":
            {
                var device = new KeyInputDevice(id);
                var result = new ActionSequence(device);
                foreach (var node in actions)
                {
                    if (node is not JsonObject action)
                    {
                        continue;
                    }

                    var value = (string?)action["value"] ?? "";
                    switch ((string?)action["type"])
                    {
                        case "pause":
                            result.AddAction(device.CreatePause(DurationOf(action)));
                            break;
                        case "keyDown":
                            result.AddAction(device.CreateKeyDown(value[0]));
                            break;
                        case "keyUp":
                            result.AddAction(device.CreateKeyUp(value[0]));
                            break;
                    }
                }

                return result;
            }

            case "wheel":
            {
                var device = new WheelInputDevice(id);
                var result = new ActionSequence(device);
                foreach (var node in actions)
                {
                    if (node is not JsonObject action)
                    {
                        continue;
                    }

                    switch ((string?)action["type"])
                    {
                        case "pause":
                            result.AddAction(device.CreatePause(DurationOf(action)));
                            break;
                        case "scroll":
                        {
                            var (originX, originY) = await ResolveOriginAsync(action, mapper, context, ct).ConfigureAwait(false)
                                                     ?? mapper.ToScreen((double?)action["x"] ?? 0, (double?)action["y"] ?? 0);
                            result.AddAction(device.CreateWheelScroll(
                                CoordinateOrigin.Viewport,
                                originX, originY,
                                mapper.ToScreenDelta((double?)action["deltaX"] ?? 0),
                                mapper.ToScreenDelta((double?)action["deltaY"] ?? 0),
                                DurationOf(action)));
                            break;
                        }
                    }
                }

                return result;
            }

            default:
                return null; // 'none' sources carry only pauses; timing is best-effort
        }
    }

    private async Task<Interaction> TranslateMoveAsync(JsonObject action, PointerInputDevice device, CoordinateMapper mapper, IActionsContext context, CancellationToken ct)
    {
        var duration = DurationOf(action);
        var offsetX = (double?)action["x"] ?? 0;
        var offsetY = (double?)action["y"] ?? 0;

        var isPointerOrigin = action["origin"] is JsonValue originValue
                              && originValue.TryGetValue<string>(out var originName)
                              && originName == "pointer";
        if (isPointerOrigin)
        {
            return device.CreatePointerMove(CoordinateOrigin.Pointer, mapper.ToScreenDelta(offsetX), mapper.ToScreenDelta(offsetY), duration);
        }

        var elementOrigin = await ResolveOriginAsync(action, mapper, context, ct).ConfigureAwait(false);
        var (x, y) = elementOrigin ?? mapper.ToScreen(offsetX, offsetY);
        return device.CreatePointerMove(CoordinateOrigin.Viewport, x, y, duration);
    }

    /// <summary>Resolves an element origin to screen coordinates (element center + offsets), or null for non-element origins.</summary>
    private async Task<(int X, int Y)?> ResolveOriginAsync(JsonObject action, CoordinateMapper mapper, IActionsContext context, CancellationToken ct)
    {
        if (action["origin"] is not JsonObject origin || origin[ElementKey] is null)
        {
            return null;
        }

        var rect = await context.ExecuteScriptAsync(ViewportRectScript, new JsonArray(origin.DeepClone()), ct).ConfigureAwait(false);
        var centerX = ((double?)rect?["x"] ?? 0) + ((double?)rect?["width"] ?? 0) / 2 + ((double?)action["x"] ?? 0);
        var centerY = ((double?)rect?["y"] ?? 0) + ((double?)rect?["height"] ?? 0) / 2 + ((double?)action["y"] ?? 0);
        return mapper.ToScreen(centerX, centerY);
    }

    private static TimeSpan DurationOf(JsonObject action)
        => TimeSpan.FromMilliseconds((double?)action["duration"] ?? 0);

    private static MouseButton ButtonOf(JsonObject action)
        => ((long?)action["button"] ?? 0) switch
        {
            1 => MouseButton.Middle,
            2 => MouseButton.Right,
            _ => MouseButton.Left,
        };
}

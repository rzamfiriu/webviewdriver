using System.Diagnostics;

namespace WebViewDriver.Host;

/// <summary>
/// Entry point the app under test uses to expose its webviews for automation.
/// Completely inert unless <c>WEBVIEWDRIVER_PORT</c> and <c>WEBVIEWDRIVER_TOKEN</c>
/// are present in the environment (SR2); binds 127.0.0.1 only (SR1).
/// </summary>
public static partial class WebViewDriverHost
{
    public const string PortVariable = "WEBVIEWDRIVER_PORT";
    public const string TokenVariable = "WEBVIEWDRIVER_TOKEN";

    private static readonly object Gate = new();
    private static HostServer? _server;
    private static readonly Dictionary<string, IScriptHost> PendingAttachments = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> PendingReadiness = new(StringComparer.Ordinal);

    /// <summary>
    /// Optional log sink (FR10). Messages also go to <see cref="Trace"/>.
    /// Useful for surfacing host activity in a MAUI app's own logging.
    /// </summary>
    public static Action<string>? Log { get; set; }

    private static void Emit(string message)
    {
        Trace.WriteLine(message);
        Log?.Invoke(message);
    }

    /// <summary>Whether the automation endpoint is currently listening.</summary>
    public static bool IsActive
    {
        get { lock (Gate) { return _server is not null; } }
    }

    /// <summary>
    /// Starts the endpoint if (and only if) the environment requests it.
    /// Call once at app startup, inside a Debug/QA-only compilation block.
    /// Returns true when the endpoint was started.
    /// </summary>
    public static bool TryStartFromEnvironment()
    {
        var portValue = Environment.GetEnvironmentVariable(PortVariable);
        var token = Environment.GetEnvironmentVariable(TokenVariable);
        if (string.IsNullOrEmpty(portValue))
        {
            return false; // SR2: no default port, no default-on.
        }

        if (string.IsNullOrEmpty(token))
        {
            Emit($"[WebViewDriver] {PortVariable} is set but {TokenVariable} is missing; refusing to start (SR3).");
            return false;
        }

        if (!int.TryParse(portValue, out var port) || port is < 1 or > 65535)
        {
            Emit($"[WebViewDriver] {PortVariable}='{portValue}' is not a valid port; refusing to start.");
            return false;
        }

        lock (Gate)
        {
            if (_server is not null)
            {
                return true;
            }

            var server = new HostServer(port, token);
            server.Start();
            _server = server;
            foreach (var (name, scriptHost) in PendingAttachments)
            {
                server.AttachTarget(name, scriptHost);
            }

            foreach (var (name, ready) in PendingReadiness)
            {
                server.SetTargetReady(name, ready);
            }

            PendingAttachments.Clear();
            PendingReadiness.Clear();
        }

        // SR4: prominent warning whenever active.
        Emit($"[WebViewDriver] AUTOMATION ENDPOINT ACTIVE on 127.0.0.1:{port}. " +
             "This exposes JavaScript eval inside the app to local processes holding the session token. " +
             "Never enable in production builds.");
        return true;
    }

    /// <summary>
    /// Registers a webview under a name the test side can address. Safe to call
    /// whether or not the endpoint is active; attachments made before
    /// <see cref="TryStartFromEnvironment"/> are queued.
    /// </summary>
    public static void Attach(string name, IScriptHost scriptHost)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(scriptHost);

        lock (Gate)
        {
            if (_server is not null)
            {
                _server.AttachTarget(name, scriptHost);
                Emit($"[WebViewDriver] Webview '{name}' attached and reachable for automation.");
            }
            else
            {
                PendingAttachments[name] = scriptHost;
            }
        }
    }

    /// <summary>Registers a webview via a plain evaluate-JavaScript delegate.</summary>
    public static void Attach(string name, Func<string, Task<string?>> evaluateJavaScript)
        => Attach(name, new DelegateScriptHost(evaluateJavaScript));

    /// <summary>
    /// FR9: declares a target's readiness. Test-side Connect waits for targets
    /// that declared themselves not-ready. Safe to call before the endpoint is
    /// active or before the target attaches.
    /// </summary>
    public static void SetReady(string name, bool ready = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (Gate)
        {
            if (_server is not null)
            {
                _server.SetTargetReady(name, ready);
            }
            else
            {
                PendingReadiness[name] = ready;
            }
        }
    }

    public static void Detach(string name)
    {
        lock (Gate)
        {
            _server?.DetachTarget(name);
            PendingAttachments.Remove(name);
            PendingReadiness.Remove(name);
        }
    }

    /// <summary>Stops the endpoint. Primarily for tests.</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            _server?.Dispose();
            _server = null;
            PendingAttachments.Clear();
            PendingReadiness.Clear();
        }
    }
}

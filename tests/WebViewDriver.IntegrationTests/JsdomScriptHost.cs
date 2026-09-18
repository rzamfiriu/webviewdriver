using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using WebViewDriver.Host;

namespace WebViewDriver.IntegrationTests;

/// <summary>
/// Stands in for a WKWebView: forwards scripts to a Node process running the
/// fixture document in jsdom, returning the completion value as a string —
/// the same contract WKWebView.EvaluateJavaScriptAsync provides.
/// </summary>
public sealed class JsdomScriptHost : IScriptHost, IDisposable
{
    private readonly Process _process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<string?>> _inflight = new();
    private long _nextId;

    public static string JsHostDirectory { get; } =
        typeof(JsdomScriptHost).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "JsHostDirectory").Value!;

    public JsdomScriptHost()
    {
        EnsureNodeModules();

        _process = Process.Start(new ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { "host.js", "fixture.html" },
            WorkingDirectory = JsHostDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to start the Node jsdom host.");

        _process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            var reply = JsonNode.Parse(e.Data)!;
            var id = (long)reply["id"]!;
            if (_inflight.TryRemove(id, out var pending))
            {
                if ((bool?)reply["ok"] == true)
                {
                    pending.TrySetResult((string?)reply["result"]);
                }
                else
                {
                    pending.TrySetException(new InvalidOperationException((string?)reply["error"] ?? "jsdom eval failed"));
                }
            }
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                Console.Error.WriteLine("[jsdom] " + e.Data);
            }
        };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private static void EnsureNodeModules()
    {
        if (Directory.Exists(Path.Combine(JsHostDirectory, "node_modules", "jsdom")))
        {
            return;
        }

        var npm = Process.Start(new ProcessStartInfo
        {
            FileName = "npm",
            ArgumentList = { "install", "--no-audit", "--no-fund" },
            WorkingDirectory = JsHostDirectory,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to start npm install.");
        npm.WaitForExit();
        if (npm.ExitCode != 0)
        {
            throw new InvalidOperationException($"npm install failed with exit code {npm.ExitCode}.");
        }
    }

    public async Task<string?> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inflight[id] = pending;

        var request = new JsonObject { ["id"] = id, ["script"] = script }.ToJsonString();
        await _process.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);

        return await pending.Task.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Already gone.
        }

        _process.Dispose();
    }
}

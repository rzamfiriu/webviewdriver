using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using WebViewDriver.Host;

namespace WebViewDriver.Tests;

internal static class Ports
{
    public static int GetFree()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>QR2: a fake transport so the executor is testable with no app.</summary>
internal sealed class FakeScriptHost : IScriptHost, IDisposable
{
    public ConcurrentQueue<string> CannedResponses { get; } = new();
    public List<string> ReceivedScripts { get; } = new();
    public Func<string, string?>? Responder { get; set; }
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public async Task<string?> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken)
    {
        lock (ReceivedScripts)
        {
            ReceivedScripts.Add(script);
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (Responder is not null)
        {
            return Responder(script);
        }

        return CannedResponses.TryDequeue(out var response)
            ? response
            : throw new InvalidOperationException("FakeScriptHost has no canned response left for: " + script);
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeScreenshotScriptHost : IScriptHost, IScreenshotProvider
{
    public byte[] Png { get; set; } = { 0x89, 0x50, 0x4E, 0x47 };
    public ScreenshotRegion? LastRequestedRegion { get; private set; }

    public Task<string?> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken)
        => Task.FromResult<string?>("null");

    public Task<byte[]> TakePngScreenshotAsync(ScreenshotRegion? region, CancellationToken cancellationToken)
    {
        LastRequestedRegion = region;
        return Task.FromResult(Png);
    }
}

using System.Net;
using System.Net.Sockets;
using OpenQA.Selenium;
using WebViewDriver.Host;
using WebViewDriver.Selenium;
using Xunit;

namespace WebViewDriver.IntegrationTests;

/// <summary>
/// Boots the full stack once for all tests, through the public API only:
/// env vars -> WebViewDriverHost.TryStartFromEnvironment -> Attach(jsdom "webview")
/// -> WebViewDriverClient.Connect -> RemoteWebDriver.
/// </summary>
public sealed class DriverFixture : IDisposable
{
    public const string Token = "integration-test-token";

    public int Port { get; }
    public JsdomScriptHost Jsdom { get; }
    public IWebDriver Driver { get; }

    public DriverFixture()
    {
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        Environment.SetEnvironmentVariable(WebViewDriverHost.PortVariable, Port.ToString());
        Environment.SetEnvironmentVariable(WebViewDriverHost.TokenVariable, Token);
        Assert.True(WebViewDriverHost.TryStartFromEnvironment());

        Jsdom = new JsdomScriptHost();
        WebViewDriverHost.Attach("main", Jsdom);

        Driver = WebViewDriverClient.Connect(Port, Token, target: "main", timeout: TimeSpan.FromSeconds(60));
    }

    public void Dispose()
    {
        Driver.Quit();
        Driver.Dispose();
        Jsdom.Dispose();
        WebViewDriverHost.Stop();
    }
}

[CollectionDefinition("driver")]
public sealed class DriverCollection : ICollectionFixture<DriverFixture>
{
}

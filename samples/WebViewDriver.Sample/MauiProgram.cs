using Microsoft.Maui.Handlers;
using WebViewDriver.Host;

namespace WebViewDriver.Sample;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

#if DEBUG
        // SR2/SR4: inert unless WEBVIEWDRIVER_PORT + WEBVIEWDRIVER_TOKEN are set,
        // and the whole block is compiled out of Release builds.
        WebViewDriverHost.TryStartFromEnvironment();

        HybridWebViewHandler.Mapper.AppendToMapping("WebViewDriver", (handler, _) =>
        {
#if MACCATALYST || IOS
            WebViewDriverHost.Attach("main", (WebKit.WKWebView)handler.PlatformView);
#endif
        });
#endif

        return builder.Build();
    }
}

public sealed class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
        => new(new MainPage());
}

public sealed class MainPage : ContentPage
{
    public MainPage()
    {
        Title = "WebViewDriver Sample";
        Content = new HybridWebView
        {
            HybridRoot = "wwwroot",
            DefaultFile = "index.html",
        };
    }
}

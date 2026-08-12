namespace DeveMobileLPR.App.Controls;

internal static class PlatformMapWebViewConfigurator
{
    public static void Configure(HybridWebView webView, string userAgent)
    {
        if (webView.Handler?.PlatformView is not Microsoft.Maui.Platform.MauiHybridWebView windowsView)
        {
            return;
        }

        windowsView.RunAfterInitialize(() =>
        {
            var settings = windowsView.CoreWebView2?.Settings;
            if (settings is null || settings.UserAgent.StartsWith(userAgent, StringComparison.Ordinal))
            {
                return;
            }

            settings.UserAgent = $"{userAgent} {settings.UserAgent}";
        });
    }
}

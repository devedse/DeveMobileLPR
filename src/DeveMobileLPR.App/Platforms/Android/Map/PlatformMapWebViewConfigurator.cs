namespace DeveMobileLPR.App.Controls;

internal static class PlatformMapWebViewConfigurator
{
    public static void Configure(HybridWebView webView, string userAgent)
    {
        if (webView.Handler?.PlatformView is not global::Android.Webkit.WebView androidView)
        {
            return;
        }

        var existing = androidView.Settings.UserAgentString ?? string.Empty;
        if (!existing.StartsWith(userAgent, StringComparison.Ordinal))
        {
            androidView.Settings.UserAgentString = $"{userAgent} {existing}";
        }
    }
}

using WebKit;

namespace DeveMobileLPR.App.Controls;

internal static class PlatformMapWebViewConfigurator
{
    public static void Configure(HybridWebView webView, string userAgent)
    {
        if (webView.Handler?.PlatformView is not WKWebView iosView)
        {
            return;
        }

        var existing = iosView.CustomUserAgent ?? string.Empty;
        if (!existing.StartsWith(userAgent, StringComparison.Ordinal))
        {
            iosView.CustomUserAgent = string.IsNullOrEmpty(existing)
                ? userAgent
                : $"{userAgent} {existing}";
        }
    }
}

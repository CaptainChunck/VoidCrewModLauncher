using System.Diagnostics.CodeAnalysis;

namespace VoidCrewModLauncher.Core;

/// <summary>
/// Which links the launcher opens in the browser: only web pages (absolute http or https addresses with a host). A mod's
/// website field and its README come from its author, so a file path, a share or another kind of link is never opened.
/// </summary>
public static class WebLink
{
    public static bool IsWebPage([NotNullWhen(true)] string? text, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp) return false;
        if (string.IsNullOrEmpty(parsed.Host)) return false;
        uri = parsed;
        return true;
    }
}

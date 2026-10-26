using System.Security.Cryptography;

namespace Nite.Identity.Security;

/// <summary>
/// Response headers that limit what an injected script, a framing page or a sniffing browser can
/// do. The Content-Security-Policy allows scripts and styles from this origin plus, for the few
/// Nite writes inline in development (the import map, hot-updated CSS), a fresh nonce per request,
/// which <c>NiteOptions.CspNonce</c> hands to Nite.
/// </summary>
public static class SecurityHeaders
{
    private static readonly object NonceKey = new();

    public static string? Nonce(HttpContext context) => context.Items[NonceKey] as string;

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        context.Items[NonceKey] = nonce;

        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = string.Join("; ",
            "default-src 'none'",
            $"script-src 'self' 'nonce-{nonce}'",
            $"style-src 'self' 'nonce-{nonce}'",
            "img-src 'self' data:",
            "font-src 'self'",
            "connect-src 'self'",          // the API, and in development Nite's hot-update socket
            "manifest-src 'self'",
            "base-uri 'none'",
            "form-action 'self'",
            "frame-ancestors 'none'",
            "object-src 'none'");
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        await next();
    });
}

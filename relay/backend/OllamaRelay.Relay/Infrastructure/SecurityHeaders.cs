namespace OllamaRelay.Relay.Infrastructure;

public static class SecurityHeaders
{
    private const string Csp =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; " +
        "connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            var headers = http.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers.XFrameOptions = "DENY";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            if (http.Request.IsHttps)
            {
                headers.StrictTransportSecurity = "max-age=31536000";
            }

            var path = http.Request.Path;
            if (!path.StartsWithSegments("/api") && !path.StartsWithSegments("/v1"))
            {
                headers.ContentSecurityPolicy = Csp;
            }

            if (path.StartsWithSegments("/mgmt"))
            {
                headers.CacheControl = "no-store";
            }

            await next();
        });
}

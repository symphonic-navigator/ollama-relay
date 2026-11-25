using Microsoft.AspNetCore.Diagnostics;

namespace OllamaRelay.Relay.Infrastructure;

/// <summary>
/// Logs only the exception type and stack trace. Exception messages can quote input (a JSON parser
/// error, for example), so they are never logged and never returned to the client.
/// </summary>
public sealed class ContentSafeExceptionHandler(ILogger<ContentSafeExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        logger.LogError("unhandled {Type} on {Method} {Path}\n{Stack}", exception.GetType().FullName,
            http.Request.Method, http.Request.Path.Value, exception.StackTrace);
        if (!http.Response.HasStarted)
        {
            http.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await http.Response.WriteAsJsonAsync(new { error = "internal error" }, ct);
        }

        return true;
    }
}

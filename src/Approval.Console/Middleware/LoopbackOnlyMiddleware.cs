using System.Net;

namespace Approval.Console.Middleware;

public sealed class LoopbackOnlyMiddleware(RequestDelegate next, ILogger<LoopbackOnlyMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        HttpRequest request = context.Request;
        string origin = request.Headers.Origin.ToString();
        bool invalidOrigin = origin.Length > 0 &&
            origin != $"{request.Scheme}://{request.Host}";
        bool denied = context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) ||
            request.Host.Host is not ("127.0.0.1" or "localhost") || invalidOrigin ||
            request.Headers["Sec-Fetch-Site"] == "cross-site" ||
            (HttpMethods.IsPost(request.Method) &&
                (request.Headers["X-Approval-Console"] != "spike" || !request.HasJsonContentType()));
        if (denied)
        {
            logger.LogWarning("Rejected non-local, cross-origin, or missing-header request to {Path}", request.Path);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { title = "Loopback same-origin demo requests only. POST requires JSON and X-Approval-Console: spike." },
                context.RequestAborted);
            return;
        }

        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'self'; connect-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        await next(context);
    }
}

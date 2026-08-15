namespace CitusManager.Middleware;

public sealed class SecurityHeadersMiddleware(RequestDelegate next, bool isDevelopment)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            var developmentConnections = isDevelopment
                ? " ws://localhost:* wss://localhost:*"
                : string.Empty;
            headers.ContentSecurityPolicy =
                "default-src 'self'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'; " +
                "script-src 'self' https://cdn.jsdelivr.net; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
                $"font-src 'self'; connect-src 'self' https://cdn.jsdelivr.net{developmentConnections}; object-src 'none'";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            return Task.CompletedTask;
        });
        await next(context);
    }
}

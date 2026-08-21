namespace CitusManager.Middleware;

/// <summary>Applies browser cache policies to static UI assets.</summary>
public sealed class StaticAssetCacheMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> CacheableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif", ".css", ".gif", ".ico", ".jpeg", ".jpg", ".js", ".map", ".png", ".svg", ".webp", ".woff", ".woff2"
    };

    public async Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method) &&
            CacheableExtensions.Contains(Path.GetExtension(context.Request.Path)))
        {
            context.Response.OnStarting(() =>
            {
                if (context.Response.StatusCode is >= StatusCodes.Status200OK and < StatusCodes.Status300MultipleChoices)
                {
                    context.Response.Headers.CacheControl = context.Request.Query.ContainsKey("v")
                        ? "public, max-age=31536000, immutable"
                        : "public, max-age=86400";
                }

                return Task.CompletedTask;
            });
        }

        await next(context);
    }
}

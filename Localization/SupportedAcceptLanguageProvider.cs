using System.Globalization;
using Microsoft.AspNetCore.Localization;

namespace CitusManager.Localization;

public sealed class SupportedAcceptLanguageProvider(IAppLanguageCatalog languages) : RequestCultureProvider
{
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var requested = httpContext.Request.Headers.AcceptLanguage.ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Parse)
            .Where(item => item.Quality > 0)
            .OrderByDescending(item => item.Quality);

        foreach (var item in requested)
        {
            var culture = languages.Normalize(item.Language);
            if (culture is not null)
                return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(culture, culture));
        }

        return Task.FromResult<ProviderCultureResult?>(null);
    }

    private static (string Language, decimal Quality) Parse(string item)
    {
        var parts = item.Split(';', StringSplitOptions.TrimEntries);
        if (parts[0] is "" or "*") return (string.Empty, 0);
        var quality = 1m;
        foreach (var option in parts.Skip(1))
        {
            if (option.StartsWith("q=", StringComparison.OrdinalIgnoreCase) &&
                decimal.TryParse(option.AsSpan(2), NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var parsed))
                quality = Math.Clamp(parsed, 0, 1);
        }

        return (parts[0], quality);
    }
}

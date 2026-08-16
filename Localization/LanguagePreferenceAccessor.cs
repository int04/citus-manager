using System.Security.Claims;
using Microsoft.AspNetCore.Localization;

namespace CitusManager.Localization;

public interface ILanguagePreferenceAccessor
{
    string? GetExplicitCulture(HttpContext context);
}

public sealed class LanguagePreferenceAccessor(IAppLanguageCatalog catalog) : ILanguagePreferenceAccessor
{
    public const string CultureClaimType = "citus_manager:preferred_culture";

    public string? GetExplicitCulture(HttpContext context)
    {
        var claim = context.User.FindFirstValue(CultureClaimType);
        var normalized = catalog.Normalize(claim);
        if (normalized is not null) return normalized;

        if (!context.Request.Cookies.TryGetValue(CookieRequestCultureProvider.DefaultCookieName, out var cookie))
            return null;
        var parsed = CookieRequestCultureProvider.ParseCookieValue(cookie);
        return catalog.Normalize(parsed?.UICultures.FirstOrDefault().Value);
    }
}

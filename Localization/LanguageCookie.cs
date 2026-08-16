using Microsoft.AspNetCore.Localization;

namespace CitusManager.Localization;

public static class LanguageCookie
{
    public static void Write(HttpResponse response, string culture, bool secure)
    {
        response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = secure,
                Path = "/"
            });
    }

    public static void Delete(HttpResponse response, bool secure) => response.Cookies.Delete(
        CookieRequestCultureProvider.DefaultCookieName,
        new CookieOptions { HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Lax, Secure = secure, Path = "/" });
}

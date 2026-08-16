using CitusManager.Domain;
using CitusManager.Localization;
using CitusManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace CitusManager.Controllers;

public sealed class SettingsController(
    IAppLanguageCatalog languages,
    ILanguagePreferenceAccessor preferenceAccessor,
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    IStringLocalizer<SharedResource> text) : Controller
{
    [HttpGet("/Settings/Language")]
    public IActionResult Language() => View(new LanguageSettingsViewModel(
        preferenceAccessor.GetExplicitCulture(HttpContext) ?? "auto",
        languages.SupportedLanguages));

    [HttpPost("/Settings/Language"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Language(LanguagePreferenceViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return View(new LanguageSettingsViewModel(model.Preference, languages.SupportedLanguages));

        var result = await SavePreference(model.Preference, cancellationToken);
        if (result is not null)
        {
            ModelState.AddModelError(nameof(model.Preference), result);
            return View(new LanguageSettingsViewModel(model.Preference, languages.SupportedLanguages));
        }

        TempData["Notice"] = text["Language.Saved"].Value;
        return RedirectToAction(nameof(Language));
    }

    [HttpPost("/Settings/Language/Set"), AllowAnonymous, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetLanguage(
        LanguagePreferenceViewModel model,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return BadRequest();
        var error = await SavePreference(model.Preference, cancellationToken);
        if (error is not null) return BadRequest();
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Action("Index", "Home")!);
    }

    private async Task<string?> SavePreference(string preference, CancellationToken cancellationToken)
    {
        var culture = string.Equals(preference, "auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : languages.Normalize(preference);
        if (culture is null && !string.Equals(preference, "auto", StringComparison.OrdinalIgnoreCase))
            return text["Language.Unsupported"].Value;

        if (User.Identity?.IsAuthenticated == true)
        {
            var user = await users.GetUserAsync(User);
            if (user is null) return text["Language.UserUnavailable"].Value;
            user.PreferredCulture = culture;
            var update = await users.UpdateAsync(user);
            if (!update.Succeeded) return text["Language.SaveFailed"].Value;
            await signIn.RefreshSignInAsync(user);
        }

        if (culture is null) LanguageCookie.Delete(Response, Request.IsHttps);
        else LanguageCookie.Write(Response, culture, Request.IsHttps);
        return null;
    }
}

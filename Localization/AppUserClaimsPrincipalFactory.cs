using CitusManager.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CitusManager.Localization;

public sealed class AppUserClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    IOptions<IdentityOptions> options,
    IAppLanguageCatalog languages)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole<Guid>>(userManager, roleManager, options)
{
    protected override async Task<System.Security.Claims.ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        var culture = languages.Normalize(user.PreferredCulture);
        if (culture is not null) identity.AddClaim(new(LanguagePreferenceAccessor.CultureClaimType, culture));
        return identity;
    }
}

using Microsoft.AspNetCore.Identity;

namespace CitusManager.Domain;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
}

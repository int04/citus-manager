using System.ComponentModel.DataAnnotations;

namespace CitusManager.Models;

public sealed record UserListItemViewModel(Guid Id, string Email, string DisplayName, IReadOnlyList<string> Roles, bool Locked);

public sealed class CreateUserViewModel
{
    [Required, MaxLength(120)] public string DisplayName { get; set; } = string.Empty;
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    [Required, MinLength(12), DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
    [Required] public string Role { get; set; } = "Viewer";
}

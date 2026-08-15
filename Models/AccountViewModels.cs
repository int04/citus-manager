using System.ComponentModel.DataAnnotations;

namespace CitusManager.Models;

public sealed class LoginViewModel
{
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    [Required, DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
    public bool RememberMe { get; set; }
}

public sealed class SetupViewModel
{
    [Required, MaxLength(120)] public string DisplayName { get; set; } = string.Empty;
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    [Required, MinLength(12), DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
    [Required, Compare(nameof(Password)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = string.Empty;
}

using System.ComponentModel.DataAnnotations;
using CitusManager.Localization;

namespace CitusManager.Models;

public sealed record LanguagePreferenceViewModel
{
    [Required, MaxLength(16)]
    public string Preference { get; init; } = "auto";
}

public sealed record LanguageSettingsViewModel(
    string Preference,
    IReadOnlyList<SupportedLanguage> SupportedLanguages);

namespace CitusManager.Localization;

public sealed class AppLocalizationOptions
{
    public const string SectionName = "Localization";
    public string DefaultCulture { get; set; } = "en-US";
    public List<SupportedLanguage> SupportedLanguages { get; set; } =
    [
        new() { Code = "en-US", Name = "English", NativeName = "English" },
        new() { Code = "vi-VN", Name = "Vietnamese", NativeName = "Tiếng Việt" }
    ];
}

public sealed class SupportedLanguage
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NativeName { get; set; } = string.Empty;
}

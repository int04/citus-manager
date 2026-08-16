using System.Globalization;
using Microsoft.Extensions.Options;

namespace CitusManager.Localization;

public interface IAppLanguageCatalog
{
    string DefaultCulture { get; }
    IReadOnlyList<SupportedLanguage> SupportedLanguages { get; }
    bool IsSupported(string? culture);
    string? Normalize(string? culture);
}

public sealed class AppLanguageCatalog : IAppLanguageCatalog
{
    private readonly Dictionary<string, SupportedLanguage> _languages;

    public AppLanguageCatalog(IOptions<AppLocalizationOptions> options)
    {
        SupportedLanguages = options.Value.SupportedLanguages
            .Where(x => !string.IsNullOrWhiteSpace(x.Code))
            .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToArray();
        _languages = SupportedLanguages.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
        DefaultCulture = _languages.ContainsKey(options.Value.DefaultCulture)
            ? _languages[options.Value.DefaultCulture].Code
            : "en-US";
    }

    public string DefaultCulture { get; }
    public IReadOnlyList<SupportedLanguage> SupportedLanguages { get; }
    public bool IsSupported(string? culture) => Normalize(culture) is not null;

    public string? Normalize(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture)) return null;
        if (_languages.TryGetValue(culture.Trim(), out var exact)) return exact.Code;
        try
        {
            var requested = CultureInfo.GetCultureInfo(culture.Trim());
            return SupportedLanguages.FirstOrDefault(x =>
                string.Equals(CultureInfo.GetCultureInfo(x.Code).TwoLetterISOLanguageName,
                    requested.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))?.Code;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}

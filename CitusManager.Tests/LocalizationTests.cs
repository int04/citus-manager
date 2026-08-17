using System.Security.Claims;
using System.Globalization;
using System.Resources;
using System.Xml.Linq;
using CitusManager.Localization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace CitusManager.Tests;

public sealed class LocalizationTests
{
    private static AppLanguageCatalog CreateCatalog() => new(Options.Create(new AppLocalizationOptions
    {
        DefaultCulture = "en-US",
        SupportedLanguages =
        [
            new() { Code = "en-US", Name = "English", NativeName = "English" },
            new() { Code = "vi-VN", Name = "Vietnamese", NativeName = "Tiếng Việt" }
        ]
    }));

    [Theory]
    [InlineData("vi", "vi-VN")]
    [InlineData("vi-VN", "vi-VN")]
    [InlineData("en", "en-US")]
    [InlineData("fr-FR", null)]
    [InlineData("not-a-culture", null)]
    public void Catalog_normalizes_only_supported_languages(string requested, string? expected)
    {
        Assert.Equal(expected, CreateCatalog().Normalize(requested));
    }

    [Fact]
    public void Account_claim_takes_precedence_over_cookie()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(LanguagePreferenceAccessor.CultureClaimType, "vi-VN")], "test"))
        };
        var cookie = CookieRequestCultureProvider.MakeCookieValue(new RequestCulture("en-US"));
        context.Request.Headers.Cookie = $"{CookieRequestCultureProvider.DefaultCookieName}={cookie}";

        var result = new LanguagePreferenceAccessor(CreateCatalog()).GetExplicitCulture(context);

        Assert.Equal("vi-VN", result);
    }

    [Fact]
    public void Cookie_is_used_when_account_has_no_preference()
    {
        var context = new DefaultHttpContext();
        var cookie = CookieRequestCultureProvider.MakeCookieValue(new RequestCulture("vi-VN"));
        context.Request.Headers.Cookie = $"{CookieRequestCultureProvider.DefaultCookieName}={cookie}";

        var result = new LanguagePreferenceAccessor(CreateCatalog()).GetExplicitCulture(context);

        Assert.Equal("vi-VN", result);
    }

    [Fact]
    public async Task Browser_neutral_language_maps_to_supported_specific_culture()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.AcceptLanguage = "fr-FR;q=0.9, vi;q=0.8, en;q=0.7";

        var result = await new SupportedAcceptLanguageProvider(CreateCatalog())
            .DetermineProviderCultureResult(context);

        Assert.Equal("vi-VN", result?.Cultures.Single().Value);
    }

    [Fact]
    public async Task Unsupported_browser_languages_fall_back_to_default_provider()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.AcceptLanguage = "fr-FR, de;q=0.8";

        var result = await new SupportedAcceptLanguageProvider(CreateCatalog())
            .DetermineProviderCultureResult(context);

        Assert.Null(result);
    }

    [Fact]
    public void English_and_vietnamese_resources_have_identical_semantic_keys()
    {
        var resources = FindResourcesDirectory();
        foreach (var neutral in Directory.EnumerateFiles(resources, "*.resx")
                     .Where(path => !path.EndsWith(".vi-VN.resx", StringComparison.OrdinalIgnoreCase)))
        {
            var vietnamese = Path.Combine(resources,
                $"{Path.GetFileNameWithoutExtension(neutral)}.vi-VN.resx");
            Assert.True(File.Exists(vietnamese), $"Missing Vietnamese resource for {Path.GetFileName(neutral)}");
            Assert.Equal(ReadKeys(neutral), ReadKeys(vietnamese));
        }
    }

    [Fact]
    public void Marker_types_resolve_compiled_resources_instead_of_returning_keys()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        using var provider = services.BuildServiceProvider();

        var problems = provider.GetRequiredService<IStringLocalizer<ProblemDetailsResource>>();
        var client = provider.GetRequiredService<IStringLocalizer<ClientResource>>();
        var backup = provider.GetRequiredService<IStringLocalizer<BackupResource>>();

        Assert.False(problems["Unexpected.Title"].ResourceNotFound);
        Assert.Equal("Unexpected error", problems["Unexpected.Title"].Value);
        Assert.Contains(client.GetAllStrings(true), item => item.Name == "common.close");
        Assert.False(backup["Page.Eyebrow"].ResourceNotFound);
        Assert.Equal("Logical coordinator backup", backup["Page.Eyebrow"].Value);
    }

    [Fact]
    public void Backup_resource_has_vietnamese_values()
    {
        var resources = new ResourceManager("CitusManager.Resources.BackupResource", typeof(BackupResource).Assembly);

        Assert.Equal("Sao lưu logic qua coordinator", resources.GetString("Page.Eyebrow", new CultureInfo("vi-VN")));
        Assert.Equal("Sao lưu ngay", resources.GetString("Action.BackupNow", new CultureInfo("vi-VN")));
        Assert.Equal("Áp dụng", resources.GetString("Action.Apply", new CultureInfo("vi-VN")));
    }

    private static string[] ReadKeys(string path) => XDocument.Load(path)
        .Root!
        .Elements("data")
        .Select(element => (string)element.Attribute("name")!)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string FindResourcesDirectory()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "Resources");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException("Could not locate the application Resources directory.");
    }
}

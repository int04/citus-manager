using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CitusManager;
using CitusManager.Data;
using CitusManager.Domain;
using CitusManager.Endpoints;
using CitusManager.Middleware;
using CitusManager.Localization;
using CitusManager.Security;
using CitusManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var controlConnection = builder.Configuration.GetConnectionString("ControlDatabase")
    ?? throw new InvalidOperationException("ConnectionStrings:ControlDatabase is required.");
builder.Services.AddDbContext<ControlDbContext>(options => options.UseNpgsql(controlConnection));

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(
        builder.Configuration["Security:DataProtectionKeyPath"]
        ?? Path.Combine(builder.Environment.ContentRootPath, ".keys")))
    .SetApplicationName("CitusManager");

builder.Services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
    {
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<ControlDbContext>()
    .AddDefaultTokenProviders();
builder.Services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, AppUserClaimsPrincipalFactory>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/Denied";
    options.Cookie.Name = "CitusManager.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Operator", policy => policy.RequireRole("Operator", "Admin"));
    options.AddPolicy("Admin", policy => policy.RequireRole("Admin"));
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

builder.Services.AddControllersWithViews()
    .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
    .AddDataAnnotationsLocalization(options =>
        options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(ValidationResource)))
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddValidation();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    options.SerializerOptions.PropertyNameCaseInsensitive = false;
    options.SerializerOptions.AllowDuplicateProperties = false;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.Configure<AppLocalizationOptions>(builder.Configuration.GetSection(AppLocalizationOptions.SectionName));
builder.Services.AddSingleton<IAppLanguageCatalog, AppLanguageCatalog>();
builder.Services.AddSingleton<ILanguagePreferenceAccessor, LanguagePreferenceAccessor>();
builder.Services.AddOptions<RequestLocalizationOptions>()
    .Configure<IAppLanguageCatalog>((options, languages) =>
    {
        var cultures = languages.SupportedLanguages.Select(x => new CultureInfo(x.Code)).ToArray();
        options.DefaultRequestCulture = new(languages.DefaultCulture);
        options.SupportedCultures = cultures;
        options.SupportedUICultures = cultures;
        options.FallBackToParentCultures = true;
        options.FallBackToParentUICultures = true;
        options.RequestCultureProviders =
        [
            new CustomRequestCultureProvider(context =>
            {
                var culture = languages.Normalize(context.User.FindFirst(LanguagePreferenceAccessor.CultureClaimType)?.Value);
                return Task.FromResult(culture is null ? null : new ProviderCultureResult(culture, culture));
            }),
            new CookieRequestCultureProvider(),
            new SupportedAcceptLanguageProvider(languages)
        ];
    });

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

builder.Services.AddScoped<IClusterSecretProtector, ClusterSecretProtector>();
builder.Services.AddScoped<ICitusConnectionFactory, CitusConnectionFactory>();
builder.Services.AddScoped<ICitusInspector, CitusInspector>();
builder.Services.AddScoped<ICitusMutator, CitusMutator>();
builder.Services.AddSingleton<IControlPlaneLeaseProvider, ControlPlaneLeaseProvider>();
builder.Services.AddScoped<IClusterService, ClusterService>();
builder.Services.Configure<DatabaseExplorerOptions>(builder.Configuration.GetSection("DatabaseExplorer"));
builder.Services.AddScoped<IDatabaseExplorerService, DatabaseExplorerService>();
builder.Services.AddSingleton<IQueryConsoleExecutionRegistry, QueryConsoleExecutionRegistry>();
builder.Services.AddScoped<IDatabaseQueryConsoleService, DatabaseQueryConsoleService>();
builder.Services.AddScoped<IDatabaseWorkspaceService, DatabaseWorkspaceService>();
builder.Services.AddScoped<IDatabaseRowInspectionService, DatabaseRowInspectionService>();
builder.Services.AddScoped<IDatabaseObjectService, DatabaseObjectService>();
builder.Services.AddScoped<IOperationService, OperationService>();
builder.Services.AddScoped<IOperationExecutor, OperationExecutor>();
builder.Services.AddScoped<IPrometheusCollector, PrometheusCollector>();
builder.Services.AddHostedService<OperationWorker>();
builder.Services.AddHostedService<MonitoringWorker>();
builder.Services.AddHttpClient("alerts", client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient("prometheus", client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHostedService<AlertNotificationWorker>();

var app = builder.Build();

if (builder.Configuration.GetValue<bool>("Database:AutoCreateSchema"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
    await db.Database.MigrateAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
    app.UseHsts();
}
else
{
    app.UseExceptionHandler();
    app.MapOpenApi().AllowAnonymous();
}

app.UseHttpsRedirection();
app.UseMiddleware<SecurityHeadersMiddleware>(app.Environment.IsDevelopment());
app.UseRouting();
app.UseAuthentication();
app.UseRequestLocalization();
app.UseAuthorization();

app.MapStaticAssets().AllowAnonymous();
app.MapClusterEndpoints();
app.MapOperationEndpoints();
app.MapAuditEndpoints();
app.MapMonitoringEndpoints();
app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();

public partial class Program;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CitusManager.Data;
using CitusManager.Domain;
using CitusManager.Endpoints;
using CitusManager.Middleware;
using CitusManager.Security;
using CitusManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
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

builder.Services.AddLocalization();
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var cultures = new[] { new CultureInfo("vi-VN"), new CultureInfo("en-US") };
    options.DefaultRequestCulture = new("vi-VN");
    options.SupportedCultures = cultures;
    options.SupportedUICultures = cultures;
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
builder.Services.AddScoped<IDatabaseWorkspaceService, DatabaseWorkspaceService>();
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
app.UseRequestLocalization();
app.UseRouting();
app.UseAuthentication();
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

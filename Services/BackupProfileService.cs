using System.Text.Json;
using System.Net.Http.Json;
using CitusManager.Contracts;
using CitusManager.Data;
using CitusManager.Domain;
using CitusManager.Security;
using CitusManager.Services.BackupStorage;
using Microsoft.EntityFrameworkCore;

namespace CitusManager.Services;

public interface IBackupProfileService
{
    Task<BackupProfileMutationResponse> SaveStorageAsync(Guid? id, SaveStorageProfileRequest request, Guid actorId, CancellationToken ct);
    Task TestStorageAsync(Guid id, CancellationToken ct);
    Task DisableStorageAsync(Guid id, Guid actorId, CancellationToken ct);
    Task<BackupProfileMutationResponse> SaveNotificationAsync(Guid? id, SaveNotificationProfileRequest request, Guid actorId, CancellationToken ct);
    Task TestNotificationAsync(Guid id, CancellationToken ct);
    Task DisableNotificationAsync(Guid id, Guid actorId, CancellationToken ct);
    Task<BackupProfileMutationResponse> SaveTemplateAsync(Guid? id, SaveBackupTemplateRequest request, Guid actorId, CancellationToken ct);
    Task DisableTemplateAsync(Guid id, Guid actorId, CancellationToken ct);
    Task<Uri> CreateGoogleAuthorizeUriAsync(Guid id, Guid actorId, string redirectUri, string? returnUrl, CancellationToken ct);
    Task<string> CompleteGoogleOAuthAsync(string code, string state, string redirectUri, CancellationToken ct);
}

public sealed class BackupProfileService(
    ControlDbContext db,
    IBackupSecretProtector secrets,
    IBackupStorageProviderFactory storageFactory,
    INotificationSender notifications,
    IHttpClientFactory clients) : IBackupProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<BackupProfileMutationResponse> SaveStorageAsync(
        Guid? id, SaveStorageProfileRequest request, Guid actorId, CancellationToken ct)
    {
        var profile = id is null ? null : await db.StorageProfiles.Include(x => x.Versions).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (id is not null && profile is null) throw new KeyNotFoundException("Storage profile not found.");
        profile ??= new StorageProfile { Name = request.Name.Trim(), Type = request.Type };
        var previous = profile.Versions.OrderByDescending(x => x.Version).FirstOrDefault();
        profile.Name = request.Name.Trim(); profile.Type = request.Type; profile.IsEnabled = true;
        profile.CurrentVersion = previous is null ? 1 : previous.Version + 1; profile.UpdatedAt = DateTimeOffset.UtcNow;
        var version = new StorageProfileVersion
        {
            StorageProfileId = profile.Id, Version = profile.CurrentVersion, Type = request.Type,
            LocalSubdirectory = request.LocalSubdirectory, Endpoint = request.Endpoint, Bucket = request.Bucket,
            Region = request.Region, ObjectPrefix = request.ObjectPrefix, GoogleDriveFolderId = request.GoogleDriveFolderId,
            ProtectedAccessKey = request.Type == StorageType.S3Compatible ? ProtectOrKeep(request.AccessKey, previous?.ProtectedAccessKey) : null,
            ProtectedSecretKey = request.Type == StorageType.S3Compatible ? ProtectOrKeep(request.SecretKey, previous?.ProtectedSecretKey) : null,
            ProtectedGoogleClientId = request.Type == StorageType.GoogleDrive ? ProtectOrKeep(request.GoogleClientId, previous?.ProtectedGoogleClientId) : null,
            ProtectedGoogleClientSecret = request.Type == StorageType.GoogleDrive ? ProtectOrKeep(request.GoogleClientSecret, previous?.ProtectedGoogleClientSecret) : null,
            ProtectedGoogleRefreshToken = request.Type == StorageType.GoogleDrive ? ProtectOrKeep(request.GoogleRefreshToken, previous?.ProtectedGoogleRefreshToken) : null
        };
        ValidateStorage(version);
        profile.Versions.Add(version);
        if (id is null) db.StorageProfiles.Add(profile);
        db.AuditEvents.Add(ClusterService.Audit(actorId, id is null ? "backup.storage.create" : "backup.storage.update",
            "storage-profile", profile.Id, new { profile.Name, profile.Type, profile.CurrentVersion }));
        await db.SaveChangesAsync(ct);
        return new(profile.Id, profile.Name, profile.Type.ToString(), profile.CurrentVersion, profile.IsEnabled);
    }

    public async Task TestStorageAsync(Guid id, CancellationToken ct)
    {
        var version = await CurrentStorageAsync(id, ct);
        await storageFactory.Create(version).TestConnectionAsync(ct);
    }

    public async Task DisableStorageAsync(Guid id, Guid actorId, CancellationToken ct)
    {
        var profile = await db.StorageProfiles.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Storage profile not found.");
        if (await db.ClusterBackupPolicies.AnyAsync(x => x.Storages.Any(y => y.StorageProfileId == id && y.IsEnabled), ct))
            throw new InvalidOperationException("Storage profile is selected by an active policy.");
        profile.IsEnabled = false; profile.UpdatedAt = DateTimeOffset.UtcNow;
        db.AuditEvents.Add(ClusterService.Audit(actorId, "backup.storage.disable", "storage-profile", id, new { }));
        await db.SaveChangesAsync(ct);
    }

    public async Task<BackupProfileMutationResponse> SaveNotificationAsync(
        Guid? id, SaveNotificationProfileRequest request, Guid actorId, CancellationToken ct)
    {
        var profile = id is null ? null : await db.NotificationProfiles.Include(x => x.Versions).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (id is not null && profile is null) throw new KeyNotFoundException("Notification profile not found.");
        profile ??= new NotificationProfile { Name = request.Name.Trim(), Type = request.Type };
        var previous = profile.Versions.OrderByDescending(x => x.Version).FirstOrDefault();
        profile.Name = request.Name.Trim(); profile.Type = request.Type; profile.IsEnabled = true;
        profile.CurrentVersion = previous is null ? 1 : previous.Version + 1; profile.UpdatedAt = DateTimeOffset.UtcNow;
        var version = new NotificationProfileVersion
        {
            NotificationProfileId = profile.Id, Version = profile.CurrentVersion, Type = request.Type,
            SmtpHost = request.SmtpHost, SmtpPort = request.SmtpPort, SmtpUseTls = request.SmtpUseTls,
            SmtpFrom = request.SmtpFrom, SmtpUsername = request.SmtpUsername,
            ProtectedSmtpPassword = request.Type == NotificationType.Email ? ProtectOrKeep(request.SmtpPassword, previous?.ProtectedSmtpPassword) : null,
            EmailRecipientsJson = JsonSerializer.Serialize(request.EmailRecipients.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)),
            ProtectedTelegramBotToken = request.Type == NotificationType.Telegram ? ProtectOrKeep(request.TelegramBotToken, previous?.ProtectedTelegramBotToken) : null,
            TelegramTargetsJson = JsonSerializer.Serialize(request.TelegramTargets)
        };
        ValidateNotification(version);
        profile.Versions.Add(version);
        if (id is null) db.NotificationProfiles.Add(profile);
        db.AuditEvents.Add(ClusterService.Audit(actorId, id is null ? "backup.notification.create" : "backup.notification.update",
            "notification-profile", profile.Id, new { profile.Name, profile.Type, profile.CurrentVersion }));
        await db.SaveChangesAsync(ct);
        return new(profile.Id, profile.Name, profile.Type.ToString(), profile.CurrentVersion, profile.IsEnabled);
    }

    public async Task TestNotificationAsync(Guid id, CancellationToken ct)
    {
        var profile = await db.NotificationProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.IsEnabled, ct)
            ?? throw new KeyNotFoundException("Notification profile not found.");
        var version = await db.NotificationProfileVersions.AsNoTracking().SingleAsync(x => x.NotificationProfileId == id && x.Version == profile.CurrentVersion, ct);
        await notifications.SendAsync(version, new("Citus Manager backup notification test", "Configuration test succeeded.", NotificationEvent.BackupSucceeded), ct);
    }

    public async Task DisableNotificationAsync(Guid id, Guid actorId, CancellationToken ct)
    {
        var profile = await db.NotificationProfiles.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Notification profile not found.");
        if (await db.ClusterBackupPolicies.AnyAsync(x => x.Notifications.Any(y => y.NotificationProfileId == id && y.IsEnabled), ct))
            throw new InvalidOperationException("Notification profile is selected by an active policy.");
        profile.IsEnabled = false; profile.UpdatedAt = DateTimeOffset.UtcNow;
        db.AuditEvents.Add(ClusterService.Audit(actorId, "backup.notification.disable", "notification-profile", id, new { }));
        await db.SaveChangesAsync(ct);
    }

    public async Task<BackupProfileMutationResponse> SaveTemplateAsync(
        Guid? id, SaveBackupTemplateRequest request, Guid actorId, CancellationToken ct)
    {
        if (request.RetentionMinimum > request.RetentionMaximum) throw new ArgumentException("Retention minimum cannot exceed maximum.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZone);
        var template = id is null ? null : await db.BackupTemplates.Include(x => x.Storages).Include(x => x.Notifications).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (id is not null && template is null) throw new KeyNotFoundException("Backup template not found.");
        template ??= new BackupTemplate { Name = request.Name.Trim() };
        template.Name = request.Name.Trim(); template.ScheduleUnit = (Domain.BackupScheduleUnit)(int)request.Unit;
        template.ScheduleInterval = request.Interval; template.MinuteOfHour = request.Minute; template.RunAtLocalTime = new(request.Hour, request.Minute);
        template.RunOnDayOfWeek = (DayOfWeek)request.DayOfWeek; template.RunOnDayOfMonth = request.DayOfMonth; template.TimeZoneId = request.TimeZone;
        template.RetryCount = request.RetryCount; template.RetentionMaxAgeDays = request.RetentionDays;
        template.RetentionMinBackups = request.RetentionMinimum; template.RetentionMaxBackups = request.RetentionMaximum;
        template.EncryptionEnabled = request.EncryptionEnabled; template.IsEnabled = true; template.UpdatedAt = DateTimeOffset.UtcNow;
        template.Version = id is null ? 1 : template.Version + 1;
        var storageIds = request.StorageProfileIds.Distinct().ToList();
        var notificationIds = request.NotificationProfileIds.Distinct().ToList();
        foreach (var item in template.Storages.Where(x => !storageIds.Contains(x.StorageProfileId)).ToList()) template.Storages.Remove(item);
        foreach (var storageId in storageIds.Where(value => template.Storages.All(x => x.StorageProfileId != value))) template.Storages.Add(new() { Template = template, StorageProfileId = storageId });
        foreach (var item in template.Notifications.Where(x => !notificationIds.Contains(x.NotificationProfileId)).ToList()) template.Notifications.Remove(item);
        foreach (var notificationId in notificationIds.Where(value => template.Notifications.All(x => x.NotificationProfileId != value))) template.Notifications.Add(new() { Template = template, NotificationProfileId = notificationId });
        if (id is null) db.BackupTemplates.Add(template);
        db.AuditEvents.Add(ClusterService.Audit(actorId, id is null ? "backup.template.create" : "backup.template.update", "backup-template", template.Id,
            new { template.Name, template.Version }));
        await db.SaveChangesAsync(ct);
        return new(template.Id, template.Name, "Template", template.Version, template.IsEnabled);
    }

    public async Task DisableTemplateAsync(Guid id, Guid actorId, CancellationToken ct)
    {
        var template = await db.BackupTemplates.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Backup template not found.");
        template.IsEnabled = false; template.UpdatedAt = DateTimeOffset.UtcNow;
        db.AuditEvents.Add(ClusterService.Audit(actorId, "backup.template.disable", "backup-template", id, new { }));
        await db.SaveChangesAsync(ct);
    }

    public async Task<Uri> CreateGoogleAuthorizeUriAsync(
        Guid id, Guid actorId, string redirectUri, string? returnUrl, CancellationToken ct)
    {
        var version = await CurrentStorageAsync(id, ct);
        if (version.Type != StorageType.GoogleDrive || string.IsNullOrWhiteSpace(version.ProtectedGoogleClientId))
            throw new InvalidOperationException("Google Drive client configuration is incomplete.");
        var safeReturn = IsLocalReturnUrl(returnUrl)
            ? returnUrl! : "/Clusters";
        var state = secrets.Protect(JsonSerializer.Serialize(new GoogleOAuthState(id, actorId, DateTimeOffset.UtcNow.AddMinutes(10), safeReturn), JsonOptions));
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = secrets.Unprotect(version.ProtectedGoogleClientId), ["redirect_uri"] = redirectUri,
            ["response_type"] = "code", ["scope"] = "https://www.googleapis.com/auth/drive.file",
            ["access_type"] = "offline", ["prompt"] = "consent", ["state"] = state
        };
        return new Uri(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString("https://accounts.google.com/o/oauth2/v2/auth", query));
    }

    public async Task<string> CompleteGoogleOAuthAsync(string code, string state, string redirectUri, CancellationToken ct)
    {
        GoogleOAuthState payload;
        try { payload = JsonSerializer.Deserialize<GoogleOAuthState>(secrets.Unprotect(state), JsonOptions)!; }
        catch (Exception exception) { throw new UnauthorizedAccessException("Google OAuth state is invalid.", exception); }
        if (payload is null || payload.ExpiresAt < DateTimeOffset.UtcNow) throw new UnauthorizedAccessException("Google OAuth state expired.");
        var profile = await db.StorageProfiles.Include(x => x.Versions).SingleOrDefaultAsync(x => x.Id == payload.ProfileId && x.IsEnabled, ct)
            ?? throw new KeyNotFoundException("Storage profile not found.");
        var current = profile.Versions.Single(x => x.Version == profile.CurrentVersion);
        var client = clients.CreateClient("backup-google-drive");
        using var response = await client.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code, ["client_id"] = secrets.Unprotect(current.ProtectedGoogleClientId!),
            ["client_secret"] = secrets.Unprotect(current.ProtectedGoogleClientSecret!), ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        }), ct);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<GoogleTokenResponse>(JsonOptions, ct);
        if (string.IsNullOrWhiteSpace(token?.RefreshToken)) throw new InvalidOperationException("Google did not return a refresh token; revoke consent and authorize again.");
        profile.CurrentVersion++;
        profile.Versions.Add(new StorageProfileVersion
        {
            StorageProfileId = profile.Id, Version = profile.CurrentVersion, Type = StorageType.GoogleDrive,
            GoogleDriveFolderId = current.GoogleDriveFolderId, ProtectedGoogleClientId = current.ProtectedGoogleClientId,
            ProtectedGoogleClientSecret = current.ProtectedGoogleClientSecret, ProtectedGoogleRefreshToken = secrets.Protect(token.RefreshToken)
        });
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        db.AuditEvents.Add(ClusterService.Audit(payload.ActorId, "backup.storage.google-oauth", "storage-profile", profile.Id,
            new { profile.CurrentVersion }));
        await db.SaveChangesAsync(ct);
        return payload.ReturnUrl;
    }

    private async Task<StorageProfileVersion> CurrentStorageAsync(Guid id, CancellationToken ct)
    {
        var profile = await db.StorageProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.IsEnabled, ct)
            ?? throw new KeyNotFoundException("Storage profile not found.");
        return await db.StorageProfileVersions.AsNoTracking().SingleAsync(x => x.StorageProfileId == id && x.Version == profile.CurrentVersion, ct);
    }
    private string? ProtectOrKeep(string? plaintext, string? previous) => string.IsNullOrWhiteSpace(plaintext) ? previous : secrets.Protect(plaintext);
    private static bool IsLocalReturnUrl(string? value) => !string.IsNullOrWhiteSpace(value) && value[0] == '/' &&
        !value.StartsWith("//", StringComparison.Ordinal) && !value.StartsWith("/\\", StringComparison.Ordinal) &&
        value.IndexOfAny(['\r', '\n']) < 0;
    private static void ValidateStorage(StorageProfileVersion x)
    {
        if (x.Type == StorageType.S3Compatible && new[] { x.Endpoint, x.Bucket, x.Region, x.ProtectedAccessKey, x.ProtectedSecretKey }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("S3-compatible endpoint, bucket, region, access key, and secret key are required.");
        if (x.Type == StorageType.GoogleDrive && new[] { x.GoogleDriveFolderId, x.ProtectedGoogleClientId, x.ProtectedGoogleClientSecret }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Google Drive folder, client ID, and client secret are required; authorize OAuth after saving.");
    }
    private static void ValidateNotification(NotificationProfileVersion x)
    {
        if (x.Type == NotificationType.Email && (string.IsNullOrWhiteSpace(x.SmtpHost) || x.SmtpPort is null || x.EmailRecipientsJson == "[]"))
            throw new ArgumentException("SMTP host, port, and recipients are required.");
        if (x.Type == NotificationType.Telegram && (string.IsNullOrWhiteSpace(x.ProtectedTelegramBotToken) || x.TelegramTargetsJson == "[]"))
            throw new ArgumentException("Telegram bot token and targets are required.");
    }
    private sealed record GoogleOAuthState(Guid ProfileId, Guid ActorId, DateTimeOffset ExpiresAt, string ReturnUrl);
    private sealed record GoogleTokenResponse([property: System.Text.Json.Serialization.JsonPropertyName("refresh_token")] string? RefreshToken);
}

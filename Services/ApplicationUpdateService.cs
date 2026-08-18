using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CitusManager.Contracts;
using CitusManager.Data;
using CitusManager.Domain;
using Microsoft.EntityFrameworkCore;

namespace CitusManager.Services;

public sealed class ApplicationUpdateOptions
{
    public const string SectionName = "Updates";
    public string? StatePath { get; set; }
    public bool ExecutionEnabled { get; set; }
}

public interface IApplicationUpdateGate
{
    bool IsClosed { get; }
}

public interface IApplicationVersionProvider
{
    string CurrentVersion { get; }
    string DisplayVersion { get; }
}

public sealed class ApplicationVersionProvider : IApplicationVersionProvider
{
    public string CurrentVersion { get; } = ApplicationUpdateService.NormalizeVersion(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    public string DisplayVersion => CurrentVersion == "Development" ? CurrentVersion : $"v{CurrentVersion}";
}

public interface IApplicationUpdateService : IApplicationUpdateGate
{
    string CurrentVersion { get; }
    Task<ApplicationUpdateResponse> GetAsync(bool refresh, CancellationToken cancellationToken);
    Task<ApplicationUpdateResponse> QueueAsync(Guid actorId, CancellationToken cancellationToken);
}

public sealed partial class ApplicationUpdateService(
    IHttpClientFactory httpClientFactory,
    IServiceScopeFactory scopeFactory,
    IQueryConsoleExecutionRegistry queryExecutions,
    IApplicationVersionProvider versionProvider,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<ApplicationUpdateService> logger) : IApplicationUpdateService
{
    private const int UpdateProtocol = 1;
    private const int ComposeGeneration = 1;
    private static readonly TimeSpan HeartbeatMaximumAge = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly SemaphoreSlim queueSync = new(1, 1);
    private readonly SemaphoreSlim cacheSync = new(1, 1);
    private readonly HashSet<Guid> reconciledRequests = [];
    private ReleaseCache? cache;
    private UpdateRequestFile? pendingRequest;
    private volatile bool gateReserved;

    public string CurrentVersion => versionProvider.CurrentVersion;

    public bool IsClosed => gateReserved || HasUpdateInProgressOnDisk(GetOptions());

    public async Task<ApplicationUpdateResponse> GetAsync(bool refresh, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var options = GetOptions();
        var sidecar = await ReadSidecarStatusAsync(options, cancellationToken);
        var pending = pendingRequest;
        if (pending is not null && sidecar?.RequestId != pending.RequestId)
            return new(CurrentVersion, pending.TargetVersion, ApplicationUpdateState.Queued, now,
                IsExecutionAvailable(options), "The update request is waiting for the updater sidecar.", pending.RequestId);
        if (sidecar is not null)
        {
            await ReconcileAuditAsync(sidecar, cancellationToken);
            if (IsTerminal(sidecar.State) && pending?.RequestId == sidecar.RequestId) pendingRequest = null;
            // A failed health wait can be followed by a late, healthy startup. If this
            // process is already the target build, report the registry state instead of
            // leaving the sidebar permanently stuck on the old failed attempt.
            if (!IsTerminal(sidecar.State) || sidecar.TargetVersion != CurrentVersion)
                return new(CurrentVersion, sidecar.TargetVersion, sidecar.State, sidecar.UpdatedAtUtc,
                    IsExecutionAvailable(options), sidecar.Message, sidecar.RequestId);
        }

        if (!IsReleaseVersion(CurrentVersion))
            return new(CurrentVersion, null, ApplicationUpdateState.Unavailable, now, false,
                "Updates are disabled for development builds.");

        try
        {
            var latest = await GetLatestReleaseAsync(refresh, cancellationToken);
            var state = CompareVersions(latest, CurrentVersion) > 0
                ? ApplicationUpdateState.Available
                : ApplicationUpdateState.Current;
            return new(CurrentVersion, latest, state, cache?.CheckedAt ?? now,
                IsExecutionAvailable(options), IsExecutionAvailable(options) ? null : "One-click update is not configured.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Unable to check GHCR releases.");
            return new(CurrentVersion, null, ApplicationUpdateState.Unavailable, now,
                IsExecutionAvailable(options), "The release registry is temporarily unavailable.");
        }
    }

    public async Task<ApplicationUpdateResponse> QueueAsync(Guid actorId, CancellationToken cancellationToken)
    {
        await queueSync.WaitAsync(cancellationToken);
        try
        {
            var options = GetOptions();
            if (!IsExecutionAvailable(options))
                throw new ApplicationUpdateUnavailableException("One-click update is not configured for this deployment.");
            if (!IsReleaseVersion(CurrentVersion))
                throw new ApplicationUpdateUnavailableException("Development builds cannot be updated automatically.");
            if (pendingRequest is not null)
                throw new ApplicationUpdateConflictException("An application update is already queued.");

            var existing = await ReadSidecarStatusAsync(options, cancellationToken);
            if (existing is not null && !IsTerminal(existing.State))
                throw new ApplicationUpdateConflictException("An application update is already in progress.");

            // Reserve the gate before registry I/O and preflight so background workers cannot
            // claim queued work between the final database check and the atomic request write.
            gateReserved = true;
            var latest = await GetLatestReleaseAsync(true, cancellationToken);
            if (CompareVersions(latest, CurrentVersion) <= 0)
                throw new ApplicationUpdateConflictException("The application is already current.");

            var blockedReason = await GetBlockedReasonAsync(cancellationToken);
            if (blockedReason is not null)
                throw new ApplicationUpdateConflictException(blockedReason);

            var request = new UpdateRequestFile(Guid.NewGuid(), latest);
            await WriteRequestAtomicallyAsync(options.StatePath!, request, cancellationToken);
            pendingRequest = request;

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            db.AuditEvents.Add(ClusterService.Audit(actorId, "application.update.requested", "application-update",
                request.RequestId, new { targetVersion = latest, currentVersion = CurrentVersion }));
            await db.SaveChangesAsync(cancellationToken);

            return new(CurrentVersion, latest, ApplicationUpdateState.Queued, timeProvider.GetUtcNow(), true,
                "The update was queued. The application will restart.", request.RequestId);
        }
        finally
        {
            // A persisted request keeps the gate closed through IsClosed; failures reopen it.
            gateReserved = false;
            queueSync.Release();
        }
    }

    private async Task<string> GetLatestReleaseAsync(bool refresh, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (!refresh && cache is { } saved && now - saved.CheckedAt < TimeSpan.FromMinutes(15))
            return saved.Version;

        await cacheSync.WaitAsync(cancellationToken);
        try
        {
            now = timeProvider.GetUtcNow();
            if (!refresh && cache is { } inside && now - inside.CheckedAt < TimeSpan.FromMinutes(15))
                return inside.Version;

            var client = httpClientFactory.CreateClient("application-updates");
            var tokenJson = await client.GetStringAsync(
                "https://ghcr.io/token?service=ghcr.io&scope=repository:int04/citus-manager:pull", cancellationToken);
            var token = JsonSerializer.Deserialize<TokenEnvelope>(tokenJson, FileJson)?.Token;
            if (string.IsNullOrWhiteSpace(token)) throw new JsonException("GHCR did not return an access token.");

            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://ghcr.io/v2/int04/citus-manager/tags/list?n=1000");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var tags = await JsonSerializer.DeserializeAsync<TagEnvelope>(stream, FileJson, cancellationToken);
            var latest = tags?.Tags?.Where(IsReleaseVersion).OrderByDescending(ParseVersion).FirstOrDefault()
                ?? throw new JsonException("GHCR did not return a valid release tag.");
            cache = new(latest, now);
            return latest;
        }
        finally
        {
            cacheSync.Release();
        }
    }

    private async Task<string?> GetBlockedReasonAsync(CancellationToken cancellationToken)
    {
        if (queryExecutions.HasActiveExecutions) return "A SQL console execution is active.";
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        if (await db.Operations.AnyAsync(x => x.Status == OperationStatus.AwaitingApproval ||
                x.Status == OperationStatus.Approved || x.Status == OperationStatus.Running ||
                x.Status == OperationStatus.Cancelling || x.Status == OperationStatus.RetryScheduled, cancellationToken))
            return "A cluster operation is active.";
        if (await db.BackupRuns.AnyAsync(x => x.Status == BackupRunStatus.Queued || x.Status == BackupRunStatus.Running ||
                x.Status == BackupRunStatus.RetryScheduled || x.Status == BackupRunStatus.Cancelling, cancellationToken))
            return "A backup is active.";
        if (await db.RestoreRuns.AnyAsync(x => x.Status == RestoreRunStatus.Queued || x.Status == RestoreRunStatus.Running ||
                x.Status == RestoreRunStatus.Cancelling, cancellationToken))
            return "A restore is active.";
        return null;
    }

    private async Task ReconcileAuditAsync(UpdateStatusFile status, CancellationToken cancellationToken)
    {
        if (!IsTerminal(status.State)) return;
        lock (reconciledRequests)
            if (!reconciledRequests.Add(status.RequestId)) return;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var action = status.State == ApplicationUpdateState.Succeeded
                ? "application.update.succeeded" : "application.update.failed";
            var resourceId = status.RequestId.ToString();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var auditLockKey = BitConverter.ToInt64(status.RequestId.ToByteArray(), 0);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({auditLockKey})", cancellationToken);
            var alreadyRecorded = await db.AuditEvents.AsNoTracking().AnyAsync(x =>
                x.Action == action && x.ResourceType == "application-update" && x.ResourceId == resourceId,
                cancellationToken);
            if (!alreadyRecorded)
            {
                db.AuditEvents.Add(ClusterService.Audit(null, action, "application-update", status.RequestId,
                    new { status.TargetVersion, status.Message }));
                await db.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            lock (reconciledRequests) reconciledRequests.Remove(status.RequestId);
            throw;
        }
    }

    private static async Task WriteRequestAtomicallyAsync(string statePath, UpdateRequestFile request, CancellationToken ct)
    {
        Directory.CreateDirectory(statePath);
        var destination = Path.Combine(statePath, "request.json");
        var temporary = Path.Combine(statePath, $"request.{request.RequestId:N}.tmp");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(request, FileJson), ct);
        File.Move(temporary, destination, true);
    }

    private static async Task<UpdateStatusFile?> ReadSidecarStatusAsync(ApplicationUpdateOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.StatePath)) return null;
        var path = Path.Combine(options.StatePath, "status.json");
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var status = await JsonSerializer.DeserializeAsync<UpdateStatusFile>(stream, FileJson, ct);
            return status is not null && IsReleaseVersion(status.TargetVersion) ? status : null;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private ApplicationUpdateOptions GetOptions() =>
        configuration.GetSection(ApplicationUpdateOptions.SectionName).Get<ApplicationUpdateOptions>() ?? new();

    private bool IsExecutionAvailable(ApplicationUpdateOptions options)
    {
        if (!options.ExecutionEnabled || string.IsNullOrWhiteSpace(options.StatePath) || !Directory.Exists(options.StatePath))
            return false;
        var path = Path.Combine(options.StatePath, "updater-heartbeat.json");
        if (!File.Exists(path)) return false;
        try
        {
            var heartbeat = JsonSerializer.Deserialize<UpdaterHeartbeatFile>(File.ReadAllText(path), FileJson);
            if (heartbeat is null || heartbeat.Protocol != UpdateProtocol ||
                heartbeat.ComposeGeneration != ComposeGeneration) return false;
            var age = timeProvider.GetUtcNow() - heartbeat.UpdatedAtUtc;
            return age >= TimeSpan.Zero && age <= HeartbeatMaximumAge;
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool HasUpdateInProgressOnDisk(ApplicationUpdateOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.StatePath) || !Directory.Exists(options.StatePath)) return false;
        var requestPath = Path.Combine(options.StatePath!, "request.json");
        if (File.Exists(requestPath)) return true;
        var statusPath = Path.Combine(options.StatePath!, "status.json");
        if (!File.Exists(statusPath)) return false;
        try
        {
            var status = JsonSerializer.Deserialize<UpdateStatusFile>(File.ReadAllText(statusPath), FileJson);
            return status is not null && IsReleaseVersion(status.TargetVersion) && !IsTerminal(status.State);
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool IsTerminal(ApplicationUpdateState state) =>
        state is ApplicationUpdateState.Succeeded or ApplicationUpdateState.Failed;

    public static string NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Development";
        var normalized = value.Trim().TrimStart('v', 'V').Split('+', 2)[0];
        return IsReleaseVersion(normalized) ? normalized : "Development";
    }

    public static bool IsReleaseVersion(string? value) =>
        value is not null && ReleaseVersionRegex().IsMatch(value) && TryParseVersion(value, out _);

    public static int CompareVersions(string left, string right) => ParseVersion(left).CompareTo(ParseVersion(right));

    private static long ParseVersion(string value) =>
        TryParseVersion(value, out var parsed) ? parsed : throw new ArgumentException("Invalid release version.", nameof(value));

    private static bool TryParseVersion(string value, out long parsed)
    {
        parsed = 0;
        if (!ReleaseVersionRegex().IsMatch(value)) return false;
        var parts = value.Split('.');
        if (!int.TryParse(parts[0], out var year) || !int.TryParse(parts[1], out var month) ||
            !int.TryParse(parts[2], out var day) || !int.TryParse(parts[3], out var time) || time > 2359 || time % 100 > 59)
            return false;
        try { _ = new DateTime(2000 + year, month, day, time / 100, time % 100, 0); }
        catch (ArgumentOutOfRangeException) { return false; }
        parsed = year * 100000000L + month * 1000000L + day * 10000L + time;
        return true;
    }

    [GeneratedRegex(@"^\d{2}\.\d{2}\.\d{2}\.\d{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseVersionRegex();

    private sealed record ReleaseCache(string Version, DateTimeOffset CheckedAt);
    private sealed record TokenEnvelope([property: JsonPropertyName("token")] string? Token);
    private sealed record TagEnvelope([property: JsonPropertyName("tags")] string[]? Tags);
    private sealed record UpdateRequestFile(Guid RequestId, string TargetVersion);
    private sealed record UpdateStatusFile(Guid RequestId, string TargetVersion, string? PreviousImage,
        ApplicationUpdateState State, string? Message, DateTimeOffset UpdatedAtUtc);
    private sealed record UpdaterHeartbeatFile(int Protocol, int ComposeGeneration, DateTimeOffset UpdatedAtUtc);
}

public sealed class ApplicationUpdateConflictException(string message) : InvalidOperationException(message);
public sealed class ApplicationUpdateUnavailableException(string message) : InvalidOperationException(message);

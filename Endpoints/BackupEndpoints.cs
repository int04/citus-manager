using CitusManager.Contracts;
using CitusManager.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Security.Claims;
using System.Text.Json;
using CitusManager.Data;
using CitusManager.Services.BackupArtifacts;
using Microsoft.EntityFrameworkCore;

namespace CitusManager.Endpoints;

public static class BackupEndpoints
{
    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var clusterRuns = endpoints.MapGroup("/api/clusters/{clusterId:guid}").RequireAuthorization().WithTags("Backups");
        clusterRuns.MapPost("/backup-runs", async Task<Accepted<BackupRunResponse>> (
                Guid clusterId, CreateBackupRunRequest request, ClaimsPrincipal user, IBackupService service,
                IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken) =>
            {
                await antiforgery.ValidateRequestAsync(context);
                var run = await service.CreateAsync(clusterId, request, EndpointUser.Id(user), cancellationToken);
                return TypedResults.Accepted($"/api/backup-runs/{run.Id}/progress", run);
            }).RequireAuthorization("Operator").WithName("CreateBackupRun").WithSummary("Queue an immediate logical coordinator backup");

        clusterRuns.MapPut("/backup-policy", async Task<Ok<BackupPolicyResponse>> (
                Guid clusterId, SaveBackupPolicyRequest request, ClaimsPrincipal user, IBackupService service,
                IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken) =>
            {
                await antiforgery.ValidateRequestAsync(context);
                return TypedResults.Ok(await service.SavePolicyAsync(clusterId, request, EndpointUser.Id(user), cancellationToken));
            }).RequireAuthorization("Operator").WithName("SaveBackupPolicy").WithSummary("Save the coordinator backup schedule and profile selection");

        var runs = endpoints.MapGroup("/api/backup-runs/{id:guid}").RequireAuthorization().WithTags("Backups");
        runs.MapGet("/progress", async Task<Results<Ok<BackupProgressResponse>, NotFound>> (
                Guid id, IBackupService service, CancellationToken cancellationToken) =>
            {
                var progress = await service.GetProgressAsync(id, cancellationToken);
                return progress is null ? TypedResults.NotFound() : TypedResults.Ok(progress);
            }).WithName("GetBackupProgress").WithSummary("Poll exact backup phases, bytes, throughput, retries, and destinations");
        runs.MapGet("/manifest", async (Guid id, ControlDbContext db, CancellationToken ct) =>
        {
            var row = await db.BackupRuns.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.ManifestJson, x.CitusMetadataJson, x.ApplicationConsistent }).SingleOrDefaultAsync(ct);
            if (row is null || string.IsNullOrWhiteSpace(row.ManifestJson)) return Results.NotFound();
            var manifest = JsonSerializer.Deserialize<BackupArtifactManifest>(row.ManifestJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (manifest is null) return Results.NotFound();
            CitusBackupTopology? topology = null;
            if (!string.IsNullOrWhiteSpace(row.CitusMetadataJson))
                topology = JsonSerializer.Deserialize<CitusBackupTopology>(row.CitusMetadataJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Results.Ok(new
            {
                manifest.FormatVersion, manifest.CreatedAt, manifest.Encrypted, row.ApplicationConsistent,
                manifest.FrameSizeBytes, manifest.ObjectSizeBytes, manifest.ArchivePlaintextLength,
                manifest.ArchiveSha256,
                Objects = manifest.Objects.Select(x => new { x.Index, x.Key, x.PlaintextLength, x.StoredLength, x.Sha256, FrameCount = x.Frames.Count }),
                Citus = topology is null ? null : new { topology.Database, topology.PostgreSqlVersion, topology.CitusVersion,
                    topology.DatabaseSizeBytes, NodeCount = topology.Nodes.Count, TableCount = topology.Tables.Count,
                    topology.DistributedSchemas, topology.Fingerprint, topology.CapturedAt }
            });
        }).WithName("GetSanitizedBackupManifest").WithSummary("Get checksums and sanitized Citus metadata without secrets or encryption keys");

        runs.MapPost("/cancel", async Task<Ok<BackupRunResponse>> (
                Guid id, ClaimsPrincipal user, IBackupService service, IAntiforgery antiforgery,
                HttpContext context, CancellationToken cancellationToken) =>
            {
                await antiforgery.ValidateRequestAsync(context);
                return TypedResults.Ok(await service.CancelAsync(id, EndpointUser.Id(user), cancellationToken));
            }).RequireAuthorization("Operator").WithName("CancelBackupRun").WithSummary("Request cancellation of a backup process tree and uploads");

        runs.MapPut("/pin", async Task<Ok<BackupRunResponse>> (
                Guid id, SetBackupPinnedRequest request, ClaimsPrincipal user, IBackupService service,
                IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken) =>
            {
                await antiforgery.ValidateRequestAsync(context);
                return TypedResults.Ok(await service.SetPinnedAsync(id, request.Pinned, EndpointUser.Id(user), cancellationToken));
            }).RequireAuthorization("Admin").WithName("SetBackupPinned").WithSummary("Protect or release a backup from retention cleanup");

        runs.MapDelete("", async Task<NoContent> (
                Guid id, ClaimsPrincipal user, IBackupService service, IAntiforgery antiforgery,
                HttpContext context, CancellationToken cancellationToken) =>
            {
                await antiforgery.ValidateRequestAsync(context);
                await service.DeleteAsync(id, EndpointUser.Id(user), cancellationToken);
                return TypedResults.NoContent();
            }).RequireAuthorization("Admin").WithName("DeleteBackupRun")
            .WithSummary("Delete every local/cloud artifact copy and then remove the backup record");

        runs.MapPost("/restores", async Task<Accepted<RestoreRunResponse>> (
                Guid id, CreateRestoreRunRequest request, ClaimsPrincipal user, IRestoreService service,
                IAntiforgery antiforgery, HttpContext context, CancellationToken cancellationToken) =>
            {
                await antiforgery.ValidateRequestAsync(context);
                var run = await service.CreateAsync(id, request, EndpointUser.Id(user), cancellationToken);
                return TypedResults.Accepted($"/api/restore-runs/{run.Id}/progress", run);
            }).RequireAuthorization("Operator").WithName("CreateRestoreRun").WithSummary("Validate and queue a multi-phase Citus logical restore");

        var restores = endpoints.MapGroup("/api/restore-runs/{id:guid}").RequireAuthorization().WithTags("Backups");
        restores.MapGet("/progress", async Task<Results<Ok<RestoreProgressResponse>, NotFound>> (
                Guid id, IRestoreService service, CancellationToken cancellationToken) =>
            {
                var progress = await service.GetProgressAsync(id, cancellationToken);
                return progress is null ? TypedResults.NotFound() : TypedResults.Ok(progress);
            }).WithName("GetRestoreProgress").WithSummary("Poll exact restore phases, bytes, and safe errors");

        restores.MapPost("/cancel", async Task<Ok<RestoreRunResponse>> (
                Guid id, ClaimsPrincipal user, IRestoreService service, IAntiforgery antiforgery,
                HttpContext context, CancellationToken cancellationToken) =>
            {
                await antiforgery.ValidateRequestAsync(context);
                return TypedResults.Ok(await service.CancelAsync(id, EndpointUser.Id(user), cancellationToken));
            }).RequireAuthorization("Operator").WithName("CancelRestoreRun").WithSummary("Cancel a restore or mark mutated work as recovery required");

        restores.MapPost("/resolve-recovery", async Task<Ok<RestoreRunResponse>> (
                Guid id, ResolveRestoreRecoveryRequest request, ClaimsPrincipal user,
                IRestoreService service, IAntiforgery antiforgery,
                HttpContext context, CancellationToken cancellationToken) =>
            {
                await antiforgery.ValidateRequestAsync(context);
                try
                {
                    return TypedResults.Ok(await service.ResolveRecoveryAsync(
                        id, request, EndpointUser.Id(user), cancellationToken));
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException &&
                                                  exception is not RestoreRecoveryRejectedException)
                {
                    throw new RestoreRecoveryRejectedException(exception.Message, exception);
                }
            }).RequireAuthorization("Admin")
            .WithName("ResolveRestoreRecovery")
            .WithSummary("Close a manual-recovery gate after fresh Citus health validation and Admin attestation")
            .Produces<RestoreRunResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var storageProfiles = endpoints.MapGroup("/api/backup-storage-profiles").RequireAuthorization("Operator").WithTags("Backup profiles");
        storageProfiles.MapPost("/", async (SaveStorageProfileRequest request, ClaimsPrincipal user, IBackupProfileService service,
            IAntiforgery antiforgery, HttpContext context, CancellationToken ct) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            return TypedResults.Ok(await service.SaveStorageAsync(null, request, EndpointUser.Id(user), ct));
        }).WithName("CreateBackupStorageProfile");
        storageProfiles.MapPut("/{id:guid}", async (Guid id, SaveStorageProfileRequest request, ClaimsPrincipal user, IBackupProfileService service,
            IAntiforgery antiforgery, HttpContext context, CancellationToken ct) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            return TypedResults.Ok(await service.SaveStorageAsync(id, request, EndpointUser.Id(user), ct));
        }).WithName("UpdateBackupStorageProfile");
        storageProfiles.MapPost("/{id:guid}/test", async (Guid id, IBackupProfileService service, IAntiforgery antiforgery,
            HttpContext context, CancellationToken ct) =>
        {
            await antiforgery.ValidateRequestAsync(context); await service.TestStorageAsync(id, ct); return TypedResults.NoContent();
        }).WithName("TestBackupStorageProfile");
        storageProfiles.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, IBackupProfileService service,
            IAntiforgery antiforgery, HttpContext context, CancellationToken ct) =>
        {
            await antiforgery.ValidateRequestAsync(context); await service.DisableStorageAsync(id, EndpointUser.Id(user), ct); return TypedResults.NoContent();
        }).RequireAuthorization("Admin").WithName("DisableBackupStorageProfile");
        storageProfiles.MapGet("/{id:guid}/google/authorize", async (Guid id, string? returnUrl, ClaimsPrincipal user,
            IBackupProfileService service, HttpRequest request, CancellationToken ct) =>
        {
            var callback = $"{request.Scheme}://{request.Host}{request.PathBase}/api/backup-google-oauth/callback";
            var uri = await service.CreateGoogleAuthorizeUriAsync(id, EndpointUser.Id(user), callback, returnUrl, ct);
            return Results.Redirect(uri.ToString());
        }).WithName("AuthorizeGoogleDriveBackup");

        endpoints.MapGet("/api/backup-google-oauth/callback", async (string code, string state,
            IBackupProfileService service, HttpRequest request, CancellationToken ct) =>
        {
            var callback = $"{request.Scheme}://{request.Host}{request.PathBase}/api/backup-google-oauth/callback";
            var returnUrl = await service.CompleteGoogleOAuthAsync(code, state, callback, ct);
            return Results.Redirect(returnUrl);
        }).AllowAnonymous().WithTags("Backup profiles").WithName("CompleteGoogleDriveBackupOAuth");

        var notificationProfiles = endpoints.MapGroup("/api/backup-notification-profiles").RequireAuthorization("Operator").WithTags("Backup profiles");
        notificationProfiles.MapPost("/", async (SaveNotificationProfileRequest request, ClaimsPrincipal user, IBackupProfileService service,
            IAntiforgery antiforgery, HttpContext context, CancellationToken ct) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            return TypedResults.Ok(await service.SaveNotificationAsync(null, request, EndpointUser.Id(user), ct));
        }).WithName("CreateBackupNotificationProfile");
        notificationProfiles.MapPut("/{id:guid}", async (Guid id, SaveNotificationProfileRequest request, ClaimsPrincipal user, IBackupProfileService service,
            IAntiforgery antiforgery, HttpContext context, CancellationToken ct) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            return TypedResults.Ok(await service.SaveNotificationAsync(id, request, EndpointUser.Id(user), ct));
        }).WithName("UpdateBackupNotificationProfile");
        notificationProfiles.MapPost("/{id:guid}/test", async (Guid id, IBackupProfileService service, IAntiforgery antiforgery,
            HttpContext context, CancellationToken ct) =>
        {
            await antiforgery.ValidateRequestAsync(context); await service.TestNotificationAsync(id, ct); return TypedResults.NoContent();
        }).WithName("TestBackupNotificationProfile");
        notificationProfiles.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, IBackupProfileService service,
            IAntiforgery antiforgery, HttpContext context, CancellationToken ct) =>
        {
            await antiforgery.ValidateRequestAsync(context); await service.DisableNotificationAsync(id, EndpointUser.Id(user), ct); return TypedResults.NoContent();
        }).RequireAuthorization("Admin").WithName("DisableBackupNotificationProfile");

        var templates = endpoints.MapGroup("/api/backup-templates").RequireAuthorization("Operator").WithTags("Backup profiles");
        templates.MapPost("/", async (SaveBackupTemplateRequest request, ClaimsPrincipal user, IBackupProfileService service,
            IAntiforgery antiforgery, HttpContext context, CancellationToken ct) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            return TypedResults.Ok(await service.SaveTemplateAsync(null, request, EndpointUser.Id(user), ct));
        }).WithName("CreateBackupTemplate");
        templates.MapPut("/{id:guid}", async (Guid id, SaveBackupTemplateRequest request, ClaimsPrincipal user, IBackupProfileService service,
            IAntiforgery antiforgery, HttpContext context, CancellationToken ct) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            return TypedResults.Ok(await service.SaveTemplateAsync(id, request, EndpointUser.Id(user), ct));
        }).WithName("UpdateBackupTemplate");
        templates.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, IBackupProfileService service,
            IAntiforgery antiforgery, HttpContext context, CancellationToken ct) =>
        {
            await antiforgery.ValidateRequestAsync(context); await service.DisableTemplateAsync(id, EndpointUser.Id(user), ct); return TypedResults.NoContent();
        }).RequireAuthorization("Admin").WithName("DisableBackupTemplate");

        return endpoints;
    }
}

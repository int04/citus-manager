using CitusManager.Data;
using Microsoft.EntityFrameworkCore;

namespace CitusManager.Endpoints;

public sealed record AuditEventResponse(
    long Id, DateTimeOffset OccurredAt, Guid? ActorId, string Action,
    string ResourceType, string? ResourceId, string DetailJson);

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/audit", async (
                int? take, ControlDbContext db, CancellationToken cancellationToken) =>
            {
                var limit = Math.Clamp(take ?? 100, 1, 500);
                var rows = await db.AuditEvents.AsNoTracking().OrderByDescending(x => x.OccurredAt)
                    .Take(limit).Select(x => new AuditEventResponse(
                        x.Id, x.OccurredAt, x.ActorId, x.Action, x.ResourceType, x.ResourceId, x.DetailJson))
                    .ToListAsync(cancellationToken);
                return TypedResults.Ok(rows);
            })
            .RequireAuthorization("Admin")
            .WithTags("Audit").WithName("GetAuditEvents").WithSummary("Read immutable sanitized audit events");
        return endpoints;
    }
}

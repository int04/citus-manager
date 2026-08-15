using System.Security.Claims;
using CitusManager.Data;
using CitusManager.Domain;
using CitusManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CitusManager.Controllers;

[Authorize]
public sealed class AlertsController(ControlDbContext db) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken) => View(
        await db.Alerts.AsNoTracking().Include(x => x.Cluster).OrderByDescending(x => x.LastSeenAt)
            .Take(500).ToListAsync(cancellationToken));

    [HttpPost, Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Acknowledge(long id, CancellationToken cancellationToken)
    {
        var alert = await db.Alerts.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (alert is null) return NotFound();
        if (alert.State == AlertState.Open) alert.State = AlertState.Acknowledged;
        var actorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        db.AuditEvents.Add(ClusterService.Audit(actorId, "alert.acknowledge", "alert", id,
            new { alert.ClusterId, alert.Fingerprint }));
        await db.SaveChangesAsync(cancellationToken);
        TempData["Notice"] = "Đã acknowledge alert.";
        return RedirectToAction(nameof(Index));
    }
}

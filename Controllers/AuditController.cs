using CitusManager.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CitusManager.Controllers;

[Authorize(Policy = "Admin")]
public sealed class AuditController(ControlDbContext db) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken) => View(
        await db.AuditEvents.AsNoTracking().OrderByDescending(x => x.OccurredAt)
            .Take(500).ToListAsync(cancellationToken));
}

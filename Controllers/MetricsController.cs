using CitusManager.Data;
using CitusManager.Models;
using CitusManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CitusManager.Controllers;

[Authorize]
public sealed class MetricsController(ControlDbContext db, IClusterService clusters) : Controller
{
    public async Task<IActionResult> Index(Guid clusterId, CancellationToken cancellationToken)
    {
        var cluster = await clusters.GetAsync(clusterId, cancellationToken);
        if (cluster is null) return NotFound();
        var samples = await db.MetricSamples.AsNoTracking()
            .Where(x => x.ClusterId == clusterId && x.CollectedAt >= DateTimeOffset.UtcNow.AddHours(-24))
            .OrderByDescending(x => x.CollectedAt).Take(5000).ToListAsync(cancellationToken);
        return View(new MetricsViewModel(cluster, samples));
    }
}

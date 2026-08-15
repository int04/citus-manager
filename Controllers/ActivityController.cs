using CitusManager.Data;
using CitusManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CitusManager.Controllers;

[Authorize]
public sealed class ActivityController(ControlDbContext db, ICitusInspector inspector) : Controller
{
    public async Task<IActionResult> Index(Guid clusterId, CancellationToken cancellationToken)
    {
        var cluster = await db.Clusters.AsNoTracking().SingleOrDefaultAsync(x => x.Id == clusterId, cancellationToken);
        if (cluster is null) return NotFound();
        ViewData["ClusterId"] = cluster.Id;
        ViewData["ClusterName"] = cluster.Name;
        return View(await inspector.GetActivityAsync(cluster, cancellationToken));
    }
}

using CitusManager.Models;
using CitusManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CitusManager.Controllers;

[Authorize]
public sealed class BackupsController(IBackupService backups) : Controller
{
    [HttpGet("/Backups/Cluster/{clusterId:guid}")]
    public async Task<IActionResult> Cluster(Guid clusterId, CancellationToken cancellationToken)
    {
        var page = await backups.GetClusterPageAsync(clusterId, cancellationToken);
        return page is null ? NotFound() : View(new BackupClusterViewModel(page));
    }
}

using System.Security.Claims;
using CitusManager.Contracts;
using CitusManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CitusManager.Controllers;

[Authorize]
public sealed class OperationsController(IOperationService operations) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(await operations.GetAllAsync(null, cancellationToken));

    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var operation = await operations.GetAsync(id, cancellationToken);
        return operation is null ? NotFound() : View(operation);
    }

    [HttpPost, Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        Guid clusterId, CreateOperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var operation = await operations.CreateAsync(clusterId, request, ActorId(), cancellationToken);
            TempData["Notice"] = "Plan đã tạo. Cần Admin khác phê duyệt.";
            return RedirectToAction(nameof(Details), new { id = operation.Id });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            TempData["Error"] = exception.Message;
            return RedirectToAction("Details", "Clusters", new { id = clusterId });
        }
    }

    [HttpPost, Authorize(Policy = "Admin"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await operations.ApproveAsync(id, ActorId(), cancellationToken);
            TempData["Notice"] = "Đã duyệt. Runner sẽ re-run preflight trước khi thao tác.";
        }
        catch (InvalidOperationException exception)
        {
            TempData["Error"] = exception.Message;
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await operations.CancelAsync(id, ActorId(), cancellationToken);
            TempData["Notice"] = "Đã gửi yêu cầu hủy an toàn.";
        }
        catch (InvalidOperationException exception)
        {
            TempData["Error"] = exception.Message;
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    private Guid ActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

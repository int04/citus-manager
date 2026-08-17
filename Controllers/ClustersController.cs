using System.Security.Claims;
using CitusManager.Contracts;
using CitusManager.Localization;
using CitusManager.Models;
using CitusManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Npgsql;

namespace CitusManager.Controllers;

public sealed class ClustersController(
    IClusterService clusters,
    IOperationService operations,
    ILogger<ClustersController> logger,
    IStringLocalizer<ClusterResource> text) : Controller
{
    [Authorize(Policy = "Operator")]
    public IActionResult Create() => View(new CreateClusterRequest { Name = string.Empty, Host = string.Empty });

    [HttpPost, Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateClusterRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return IsAjaxRequest()
                ? BadRequest(new ValidationProblemDetails(ModelState))
                : View(request);
        try
        {
            var cluster = await clusters.CreateAsync(request, ActorId(), cancellationToken);
            var redirectUrl = Url.Action(nameof(Details), new { id = cluster.Id })!;
            if (IsAjaxRequest()) return Ok(new { redirectUrl });
            TempData["Notice"] = text["Controller.Registered"].Value;
            return RedirectToAction(nameof(Details), new { id = cluster.Id });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Coordinator registration preflight failed.");
            if (IsAjaxRequest())
                return DatabaseProblem(text["Controller.RegisterTitle"], text["Controller.RegisterDetail"], exception);
            ModelState.AddModelError(string.Empty,
                $"{text["Controller.RegisterDetail"]}\n\n{DatabaseDiagnostic(exception)}");
            return View(request);
        }
    }

    [HttpPost, Authorize(Policy = "Operator"), ValidateAntiForgeryToken]
    public async Task<IActionResult> TestConnection(
        TestClusterConnectionRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return BadRequest(new ValidationProblemDetails(ModelState));
        try
        {
            return Ok(await clusters.TestConnectionAsync(request, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Read-only coordinator connection test failed.");
            return DatabaseProblem(text["Controller.ConnectTitle"], text["Controller.ConnectDetail"], exception);
        }
    }

    public async Task<IActionResult> Details(Guid id, bool refresh, CancellationToken cancellationToken)
    {
        var cluster = await clusters.GetAsync(id, cancellationToken);
        if (cluster is null) return NotFound();
        ClusterInventoryResponse? inventory = null;
        string? safeError = null;
        try
        {
            inventory = await clusters.RefreshAsync(id, cancellationToken, refresh);
        }
        catch
        {
            safeError = text["Controller.InventoryError"];
        }
        return View(new ClusterDetailsViewModel(cluster, inventory,
            await clusters.GetQueryEndpointsAsync(id, cancellationToken),
            await operations.GetAllAsync(id, cancellationToken), safeError));
    }

    [HttpGet]
    public async Task<IActionResult> OperationTable(Guid id, CancellationToken cancellationToken) =>
        PartialView("_OperationTable", await operations.GetAllAsync(id, cancellationToken));

    [HttpPost, Authorize(Policy = "Admin"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await clusters.DeleteAsync(id, ActorId(), cancellationToken);
        TempData["Notice"] = text["Controller.Deleted"].Value;
        return RedirectToAction("Index", "Home");
    }

    private Guid ActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private bool IsAjaxRequest() =>
        string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

    private ObjectResult DatabaseProblem(string title, string fallback, Exception exception) =>
        Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: title,
            detail: $"{fallback}\n\n{DatabaseDiagnostic(exception)}");

    private static string DatabaseDiagnostic(Exception exception)
    {
        var postgres = FindException<PostgresException>(exception);
        if (postgres is not null)
        {
            var diagnostics = new List<string> { $"PostgreSQL [{postgres.SqlState}]: {postgres.MessageText}" };
            if (!string.IsNullOrWhiteSpace(postgres.Detail)) diagnostics.Add($"Detail: {postgres.Detail}");
            if (!string.IsNullOrWhiteSpace(postgres.Hint)) diagnostics.Add($"Hint: {postgres.Hint}");
            return string.Join(Environment.NewLine, diagnostics);
        }

        var npgsql = FindException<NpgsqlException>(exception);
        return npgsql is null
            ? "Connection preflight failed before PostgreSQL returned a diagnostic."
            : $"Npgsql: {npgsql.Message}";
    }

    private static TException? FindException<TException>(Exception exception) where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is TException match) return match;
        return null;
    }
}

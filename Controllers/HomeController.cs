using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using CitusManager.Models;
using CitusManager.Services;

namespace CitusManager.Controllers;

public class HomeController(IClusterService clusters, IOperationService operations) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var model = new DashboardViewModel(
            await clusters.GetAllAsync(cancellationToken),
            await operations.GetAllAsync(null, cancellationToken));
        return View(model);
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}

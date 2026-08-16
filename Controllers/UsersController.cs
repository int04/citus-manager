using System.Security.Claims;
using CitusManager.Data;
using CitusManager.Domain;
using CitusManager.Localization;
using CitusManager.Models;
using CitusManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CitusManager.Controllers;

[Authorize(Policy = "Admin")]
public sealed class UsersController(
    UserManager<ApplicationUser> users,
    ControlDbContext db,
    IStringLocalizer<UsersResource> text) : Controller
{
    public async Task<IActionResult> Index()
    {
        var result = new List<UserListItemViewModel>();
        foreach (var user in await users.Users.OrderBy(x => x.Email).ToListAsync())
            result.Add(new(user.Id, user.Email ?? string.Empty, user.DisplayName,
                (await users.GetRolesAsync(user)).ToList(), user.LockoutEnd > DateTimeOffset.UtcNow));
        return View(result);
    }

    public IActionResult Create() => View(new CreateUserViewModel());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateUserViewModel model, CancellationToken cancellationToken)
    {
        var allowedRoles = new HashSet<string>(["Viewer", "Operator", "Admin"], StringComparer.Ordinal);
        if (!allowedRoles.Contains(model.Role)) ModelState.AddModelError(nameof(model.Role), text["InvalidRole"]);
        if (!ModelState.IsValid) return View(model);
        var user = new ApplicationUser
        {
            UserName = model.Email.Trim(),
            Email = model.Email.Trim(),
            EmailConfirmed = true,
            DisplayName = model.DisplayName.Trim()
        };
        var created = await users.CreateAsync(user, model.Password);
        if (!created.Succeeded)
        {
            foreach (var error in created.Errors) ModelState.AddModelError(string.Empty, error.Description);
            return View(model);
        }
        await users.AddToRoleAsync(user, "Viewer");
        if (model.Role != "Viewer") await users.AddToRoleAsync(user, model.Role);
        db.AuditEvents.Add(ClusterService.Audit(
            Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), "user.create", "user", user.Id,
            new { user.Email, user.DisplayName, model.Role }));
        await db.SaveChangesAsync(cancellationToken);
        TempData["Notice"] = text["Created"].Value;
        return RedirectToAction(nameof(Index));
    }
}

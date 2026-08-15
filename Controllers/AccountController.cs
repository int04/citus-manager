using CitusManager.Domain;
using CitusManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CitusManager.Controllers;

public sealed class AccountController(
    UserManager<ApplicationUser> users,
    RoleManager<IdentityRole<Guid>> roles,
    SignInManager<ApplicationUser> signIn) : Controller
{
    [AllowAnonymous]
    public async Task<IActionResult> Setup()
    {
        if (await users.Users.AnyAsync()) return RedirectToAction(nameof(Login));
        return View(new SetupViewModel());
    }

    [HttpPost, AllowAnonymous, ValidateAntiForgeryToken]
    public async Task<IActionResult> Setup(SetupViewModel model)
    {
        if (await users.Users.AnyAsync()) return NotFound();
        if (!ModelState.IsValid) return View(model);
        foreach (var roleName in new[] { "Viewer", "Operator", "Admin" })
            if (!await roles.RoleExistsAsync(roleName))
                await roles.CreateAsync(new IdentityRole<Guid>(roleName));

        var user = new ApplicationUser
        {
            UserName = model.Email.Trim(),
            Email = model.Email.Trim(),
            DisplayName = model.DisplayName.Trim(),
            EmailConfirmed = true
        };
        var result = await users.CreateAsync(user, model.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
            return View(model);
        }
        await users.AddToRolesAsync(user, ["Viewer", "Operator", "Admin"]);
        await signIn.SignInAsync(user, false);
        return RedirectToAction("Index", "Home");
    }

    [AllowAnonymous]
    public async Task<IActionResult> Login(string? returnUrl = null)
    {
        if (!await users.Users.AnyAsync()) return RedirectToAction(nameof(Setup));
        ViewData["ReturnUrl"] = returnUrl;
        return View(new LoginViewModel());
    }

    [HttpPost, AllowAnonymous, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        if (!ModelState.IsValid) return View(model);
        var result = await signIn.PasswordSignInAsync(
            model.Email.Trim(), model.Password, model.RememberMe, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, "Email hoặc mật khẩu không đúng.");
            return View(model);
        }
        return LocalRedirect(returnUrl ?? Url.Action("Index", "Home")!);
    }

    [HttpPost, Authorize, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous]
    public IActionResult Denied() => View();
}

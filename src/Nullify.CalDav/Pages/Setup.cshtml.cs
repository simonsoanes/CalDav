using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Pages;

public class SetupModel(AppDbContext db, UserService users, WriteGate gate, IConfiguration config) : AppPageModel
{
    [BindProperty, Required, Display(Name = "Username")] public string Username { get; set; } = "admin";
    [BindProperty, Display(Name = "Display name")] public string? DisplayName { get; set; }
    [BindProperty, EmailAddress] public string? Email { get; set; }
    [BindProperty, Required, DataType(DataType.Password)] public string Password { get; set; } = "";
    [BindProperty, Required, DataType(DataType.Password), Display(Name = "Confirm password")] public string ConfirmPassword { get; set; } = "";
    [BindProperty, Display(Name = "Setup token")] public string? SetupToken { get; set; }

    public bool TokenRequired => !string.IsNullOrEmpty(config["Setup:Token"]);

    public async Task<IActionResult> OnGetAsync() =>
        await db.Users.AnyAsync() ? RedirectToPage("/Index") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (TokenRequired && !TokensEqual(SetupToken, config["Setup:Token"]))
            ModelState.AddModelError(nameof(SetupToken), "The setup token is incorrect.");
        if (!Names.IsValidUsername(Username))
            ModelState.AddModelError(nameof(Username), "Usernames may contain letters, digits, '.', '_', '-' and '@'.");
        if (PasswordService.ValidateStrength(Password) is { } pwError) ModelState.AddModelError(nameof(Password), pwError);
        if (Password != ConfirmPassword) ModelState.AddModelError(nameof(ConfirmPassword), "Passwords do not match.");
        if (!ModelState.IsValid) return Page();

        User admin;
        using (await gate.EnterAsync(HttpContext.RequestAborted))
        {
            if (await db.Users.AnyAsync()) return RedirectToPage("/Index");
            admin = await users.CreateAsync(Username.Trim(), DisplayName, Email, Password, isAdmin: true);
        }
        SetupGateMiddleware.MarkComplete();

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            PrincipalFactory.Create(admin, CookieAuthenticationDefaults.AuthenticationScheme));
        StatusMessage = "Welcome! Your administrator account has been created.";
        return RedirectToPage("/Index");
    }

    private static bool TokensEqual(string? a, string? b) =>
        a != null && b != null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a.Trim()), Encoding.UTF8.GetBytes(b.Trim()));
}

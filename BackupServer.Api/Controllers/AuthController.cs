using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace BackupServer.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IConfiguration _config;

    public AuthController(IConfiguration config) => _config = config;

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginDto dto)
    {
        string validUser = _config["AuthSettings:AdminUser"] ?? "admin";
        string validPass = _config["AuthSettings:AdminPassword"];

        if (dto.Username != validUser || dto.Password != validPass)
            return Unauthorized(new { Message = "Неверный логин или пароль" });

        var claims = new List<Claim> { new(ClaimTypes.Name, dto.Username) };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7) });

        return Ok(new { Message = "Успешный вход" });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { Message = "Выход выполнен" });
    }

    [HttpGet("me")]
    public IActionResult Me()
    {
        return User.Identity?.IsAuthenticated == true
            ? Ok(new { Username = User.Identity.Name })
            : Unauthorized();
    }
}

public record LoginDto(string Username, string Password);
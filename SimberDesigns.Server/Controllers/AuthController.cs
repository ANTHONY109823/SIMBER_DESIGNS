using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimberDesigns.Server.Contracts;
using SimberDesigns.Server.Data;
using SimberDesigns.Server.Models;
using SimberDesigns.Server.Services;

namespace SimberDesigns.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class AuthController(
    AppDbContext db,
    IJwtTokenService tokens,
    PasswordHasher<User> passwordHasher) : ControllerBase
{
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6)
        {
            return BadRequest("Correo y una contraseña de al menos 6 caracteres son obligatorios.");
        }

        if (await db.Users.AnyAsync(x => x.Email == email, cancellationToken))
        {
            return Conflict("Ya existe una cuenta con ese correo.");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = string.IsNullOrWhiteSpace(request.FullName) ? email : request.FullName.Trim(),
            Role = Roles.Customer,
            CreditsBalance = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(await ToResponseAsync(user, cancellationToken));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var (user, error) = await AuthenticateAsync(request, cancellationToken);
        if (user is null)
        {
            return Unauthorized(error);
        }

        if (string.Equals(user.Role, Roles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized("Usa el acceso de administrador.");
        }

        return Ok(await ToResponseAsync(user, cancellationToken));
    }

    [HttpPost("admin-login")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> AdminLogin(LoginRequest request, CancellationToken cancellationToken)
    {
        var (user, error) = await AuthenticateAsync(request, cancellationToken);
        if (user is null)
        {
            return Unauthorized(error);
        }

        if (!string.Equals(user.Role, Roles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized("Esta ruta es solo para administradores.");
        }

        return Ok(await ToResponseAsync(user, cancellationToken));
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<AuthResponse>> Me(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var user = await db.Users.FindAsync([userId.Value], cancellationToken);
        return user is null ? Unauthorized() : Ok(await ToResponseAsync(user, cancellationToken));
    }

    private async Task<(User? User, string Error)> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(x => x.Email == email, cancellationToken);
        if (user is null)
        {
            return (null, "Credenciales inválidas.");
        }

        var result = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            return (null, "Credenciales inválidas.");
        }

        return (user, "");
    }

    private async Task<AuthResponse> ToResponseAsync(User user, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var subscription = await db.Subscriptions
            .AsNoTracking()
            .Where(s => s.UserId == user.Id && s.Status == SubscriptionStatuses.Active && s.ExpiresAt > now)
            .OrderByDescending(s => s.DailyDownloadLimit)
            .FirstOrDefaultAsync(cancellationToken);

        var downloadsToday = await db.UserDownloads.CountAsync(
            d => d.UserId == user.Id && d.DownloadedAt >= now.AddDays(-1),
            cancellationToken);

        var limit = subscription?.DailyDownloadLimit ?? 0;
        return new AuthResponse(
            tokens.Create(user, subscription?.Tier),
            user.Email,
            user.FullName,
            user.Role,
            user.CreditsBalance,
            subscription?.Tier,
            limit,
            downloadsToday);
    }
}

internal static class ClaimsPrincipalExtensions
{
    public static Guid? GetUserId(this ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? user.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return Guid.TryParse(value, out var id) ? id : null;
    }
}

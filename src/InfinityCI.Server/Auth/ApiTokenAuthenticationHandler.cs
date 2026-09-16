using System.Security.Claims;
using System.Text.Encodings.Web;
using InfinityCI.Server.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InfinityCI.Server.Auth;

/// <summary>
/// Authenticates machine/CLI callers via `Authorization: Bearer &lt;token&gt;`.
/// Tokens are user-scoped; only their PBKDF2 hash is stored, so every stored
/// token must be verified (token counts per instance are small).
/// </summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IServiceScopeFactory scopeFactory)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var raw = header["Bearer ".Length..].Trim();
        if (raw.Length < 8)
            return AuthenticateResult.NoResult();

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
        foreach (var token in await db.ApiTokens.AsNoTracking().ToListAsync())
        {
            if (!PasswordHasher.Verify(raw, token.TokenHash))
                continue;
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == token.UserId);
            if (user is null)
                continue;

            await TouchLastUsedAsync(db, token.Id);
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user.Username),
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Role, user.Role),
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
        }
        return AuthenticateResult.Fail("Invalid API token.");
    }

    /// <summary>Last-used is bookkeeping only: throttled and never allowed to fail the request.</summary>
    private static async Task TouchLastUsedAsync(CiDbContext db, long tokenId)
    {
        try
        {
            var token = await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == tokenId);
            if (token is null || token.LastUsedUtc is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(1))
                return;
            token.LastUsedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        catch
        {
            // non-fatal
        }
    }
}

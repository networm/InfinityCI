using InfinityCI.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace InfinityCI.Server.Auth;

/// <summary>
/// Named Git credentials for private repositories. Secrets are encrypted at
/// rest with ASP.NET Core Data Protection and never returned by any API.
/// </summary>
public sealed class CredentialStore(IServiceScopeFactory scopeFactory, IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("InfinityCI.GitCredentials.v1");

    public sealed record CredentialInfo(long Id, string Name, string Username, DateTimeOffset CreatedUtc);

    public IReadOnlyList<CredentialInfo> List()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Storage.CiDbContext>();
        return db.StoredCredentials
            .OrderBy(c => c.Name)
            .Select(c => new CredentialInfo(c.Id, c.Name, c.Username, c.CreatedUtc))
            .ToList();
    }

    public void Save(string name, string username, string secret)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Storage.CiDbContext>();
        var existing = db.StoredCredentials.FirstOrDefault(c => c.Name == name);
        if (existing is not null)
        {
            existing.Username = username;
            existing.EncryptedSecret = _protector.Protect(secret);
        }
        else
        {
            db.StoredCredentials.Add(new Storage.StoredCredential
            {
                Name = name,
                Username = username,
                EncryptedSecret = _protector.Protect(secret),
                CreatedUtc = DateTimeOffset.UtcNow,
            });
        }
        db.SaveChanges();
    }

    public bool Delete(string name)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Storage.CiDbContext>();
        var record = db.StoredCredentials.FirstOrDefault(c => c.Name == name);
        if (record is null)
            return false;
        db.StoredCredentials.Remove(record);
        db.SaveChanges();
        return true;
    }

    /// <summary>Resolves a named credential for checkout; null for anonymous, throws for unknown names.</summary>
    public GitCredential? Resolve(string? credentialName)
    {
        if (string.IsNullOrWhiteSpace(credentialName))
            return null;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Storage.CiDbContext>();
        var record = db.StoredCredentials.FirstOrDefault(c => c.Name == credentialName)
            ?? throw new InvalidOperationException($"Credential '{credentialName}' does not exist.");
        return new GitCredential(record.Username, _protector.Unprotect(record.EncryptedSecret));
    }
}

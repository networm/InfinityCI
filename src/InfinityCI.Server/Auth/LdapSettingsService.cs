using InfinityCI.Server.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InfinityCI.Server.Auth;

/// <summary>
/// Effective LDAP configuration. Admin-managed values live in a single-row
/// database table (encrypted bind password); when no row exists the values
/// bound from "InfinityCI:Ldap" (appsettings) apply. Changes take effect
/// immediately — no restart.
/// </summary>
public sealed class LdapSettingsService(
    IServiceScopeFactory scopeFactory,
    IOptions<LdapOptions> defaultsAccessor,
    IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("InfinityCI.LdapSettings.v1");

    /// <summary>Reads the effective configuration for authentication and UI.</summary>
    public async Task<LdapOptions> GetAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
        var record = await db.LdapSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (record is null)
            return defaultsAccessor.Value;

        var defaults = defaultsAccessor.Value;
        return new LdapOptions
        {
            Enabled = record.Enabled,
            Server = record.Server,
            Port = record.Port,
            BaseDn = record.BaseDn,
            BindDn = record.BindDn,
            BindPassword = string.IsNullOrEmpty(record.EncryptedBindPassword)
                ? ""
                : _protector.Unprotect(record.EncryptedBindPassword),
            UserSearchFilter = string.IsNullOrWhiteSpace(record.UserSearchFilter)
                ? defaults.UserSearchFilter
                : record.UserSearchFilter,
            DisplayNameAttribute = string.IsNullOrWhiteSpace(record.DisplayNameAttribute)
                ? defaults.DisplayNameAttribute
                : record.DisplayNameAttribute,
            UseSsl = record.UseSsl,
            StartTls = record.StartTls,
            AcceptAnyCertificate = record.AcceptAnyCertificate,
            AdminGroupDn = record.AdminGroupDn ?? "",
            DefaultProject = record.DefaultProject ?? "",
        };
    }

    /// <summary>Whether the values come from the database (true) or appsettings defaults.</summary>
    public async Task<(bool FromDb, bool HasBindPassword)> GetStatusAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
        var record = await db.LdapSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        return (record is not null, !string.IsNullOrEmpty(record?.EncryptedBindPassword));
    }

    /// <summary>
    /// Saves admin-provided settings. <paramref name="bindPassword"/> semantics:
    /// null keeps the stored password, an empty string clears it, any other
    /// value replaces it (stored encrypted, never returned by any API).
    /// </summary>
    public async Task SaveAsync(LdapOptions options, string? bindPassword, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CiDbContext>();
        var record = await db.LdapSettings.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (record is null)
        {
            record = new LdapSettings { Id = 1 };
            db.LdapSettings.Add(record);
        }

        record.Enabled = options.Enabled;
        record.Server = options.Server.Trim();
        record.Port = options.Port;
        record.BaseDn = options.BaseDn.Trim();
        record.BindDn = options.BindDn.Trim();
        record.UserSearchFilter = string.IsNullOrWhiteSpace(options.UserSearchFilter)
            ? defaultsAccessor.Value.UserSearchFilter
            : options.UserSearchFilter.Trim();
        record.DisplayNameAttribute = string.IsNullOrWhiteSpace(options.DisplayNameAttribute)
            ? defaultsAccessor.Value.DisplayNameAttribute
            : options.DisplayNameAttribute.Trim();
        record.UseSsl = options.UseSsl;
        record.StartTls = options.StartTls;
        record.AcceptAnyCertificate = options.AcceptAnyCertificate;
        record.AdminGroupDn = string.IsNullOrWhiteSpace(options.AdminGroupDn) ? null : options.AdminGroupDn.Trim();
        record.DefaultProject = string.IsNullOrWhiteSpace(options.DefaultProject) ? null : options.DefaultProject.Trim();

        if (bindPassword is not null)
        {
            record.EncryptedBindPassword = bindPassword.Length == 0
                ? null
                : _protector.Protect(bindPassword);
        }
        record.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

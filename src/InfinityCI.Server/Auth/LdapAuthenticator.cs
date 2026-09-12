using System.DirectoryServices.Protocols;
using System.Net;
using Microsoft.Extensions.Options;

namespace InfinityCI.Server.Auth;

/// <summary>Directory entry resolved for a successfully bound LDAP user.</summary>
public sealed record LdapUser(string Dn, string? DisplayName);

/// <summary>
/// Verifies username/password against an LDAP directory: search for the user
/// entry (optionally via a service account), then bind as that entry's DN.
/// Returns null for wrong credentials; throws LdapException when the directory
/// itself is unreachable or misconfigured.
/// </summary>
public sealed class LdapAuthenticator(IOptions<LdapOptions> optionsAccessor, ILogger<LdapAuthenticator> logger)
{
    private static readonly TimeSpan LdapTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Whether LDAP authentication is switched on in configuration.</summary>
    public bool Enabled => optionsAccessor.Value.Enabled;

    private LdapConnection Connect()
    {
        var options = optionsAccessor.Value;
        var connection = new LdapConnection(new LdapDirectoryIdentifier(options.Server, options.Port));
        connection.Timeout = LdapTimeout;
        if (options.UseSsl)
        {
            connection.SessionOptions.SecureSocketLayer = true;
            if (options.AcceptAnyCertificate)
                connection.SessionOptions.VerifyServerCertificate = (_, _) => true;
        }
        return connection;
    }

    public LdapUser? Authenticate(string username, string password)
    {
        var options = optionsAccessor.Value;
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.Server))
            return null;

        // The login name goes into a search filter — escape LDAP metacharacters
        // so it can never alter the filter structure.
        var filter = string.Format(options.UserSearchFilter, LdapFilterEscape(username.Trim()));

        string userDn;
        string? displayName = null;
        using (var connection = Connect())
        {
            if (!string.IsNullOrEmpty(options.BindDn))
                connection.Bind(new NetworkCredential(options.BindDn, options.BindPassword));
            else
                connection.Bind(); // anonymous search

            var request = new SearchRequest(options.BaseDn, filter, SearchScope.Subtree, options.DisplayNameAttribute);
            var response = (SearchResponse)connection.SendRequest(request);

            if (response.Entries.Count == 0)
            {
                logger.LogInformation("LDAP login for '{Username}': no directory entry found", username);
                return null;
            }
            var entry = response.Entries[0];
            userDn = entry.DistinguishedName;
            if (entry.Attributes[options.DisplayNameAttribute] is { } attribute && attribute.Count > 0)
                displayName = attribute[0] as string;
        }

        // Binding as the found DN is the actual credential check.
        try
        {
            using var userConnection = Connect();
            userConnection.Bind(new NetworkCredential(userDn, password));
        }
        catch (LdapException ex) when (ex.ErrorCode == 49)
        {
            logger.LogInformation("LDAP login for '{Username}': invalid credentials", username);
            return null;
        }

        logger.LogInformation("LDAP login for '{Username}': bound as {Dn}", username, userDn);
        return new LdapUser(userDn, displayName);
    }

    private static string LdapFilterEscape(string value) =>
        value.Replace(@"\", @"\5c")
             .Replace("*", @"\2a")
             .Replace("(", @"\28")
             .Replace(")", @"\29")
             .Replace("\0", @"\00");
}

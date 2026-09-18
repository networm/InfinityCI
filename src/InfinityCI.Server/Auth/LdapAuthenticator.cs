using System.DirectoryServices.Protocols;
using System.Net;

namespace InfinityCI.Server.Auth;

/// <summary>Directory entry resolved for a successfully bound LDAP user.</summary>
public sealed record LdapUser(string Dn, string? DisplayName, IReadOnlyList<string> MemberOf);

/// <summary>Step-by-step result of <see cref="LdapAuthenticator.TestConfiguration"/>.</summary>
public sealed record LdapTestStep(string Name, bool Ok, string Detail);

public sealed record LdapTestResult(bool Ok, IReadOnlyList<LdapTestStep> Steps);

/// <summary>
/// Verifies username/password against an LDAP directory: search for the user
/// entry (optionally via a service account), then bind as that entry's DN.
/// Returns null for wrong credentials; throws LdapException when the directory
/// itself is unreachable or misconfigured. The configuration is passed in by
/// the caller (DB-stored runtime settings falling back to appsettings).
/// </summary>
public class LdapAuthenticator(ILogger<LdapAuthenticator> logger)
{
    private static readonly TimeSpan LdapTimeout = TimeSpan.FromSeconds(5);

    public virtual LdapUser? Authenticate(LdapOptions options, string username, string password)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.Server))
            return null;

        // The login name goes into a search filter — escape LDAP metacharacters
        // so it can never alter the filter structure.
        var filter = string.Format(options.UserSearchFilter, LdapFilterEscape(username.Trim()));

        string userDn;
        string? displayName = null;
        var memberOf = Array.Empty<string>();
        using (var connection = Connect(options))
        {
            BindServiceAccount(connection, options);

            var request = new SearchRequest(options.BaseDn, filter, SearchScope.Subtree,
                options.DisplayNameAttribute, "memberOf");
            request.TimeLimit = LdapTimeout;
            var response = (SearchResponse)connection.SendRequest(request);

            if (response.Entries.Count == 0)
            {
                logger.LogInformation("LDAP login for '{Username}': no directory entry found", username);
                return null;
            }
            if (response.Entries.Count > 1)
                logger.LogWarning(
                    "LDAP login for '{Username}': {Count} directory entries match the filter; using the first ({Dn})",
                    username, response.Entries.Count, response.Entries[0].DistinguishedName);
            var entry = response.Entries[0];
            userDn = entry.DistinguishedName;
            if (entry.Attributes[options.DisplayNameAttribute] is { } attribute && attribute.Count > 0)
                displayName = attribute[0] as string;
            if (entry.Attributes["memberOf"] is { } groups)
                memberOf = groups.GetValues(typeof(string)).Cast<string>().ToArray();
        }

        // Binding as the found DN is the actual credential check.
        try
        {
            using var userConnection = Connect(options);
            userConnection.Bind(new NetworkCredential(userDn, password));
        }
        catch (LdapException ex) when (ex.ErrorCode == 49)
        {
            logger.LogInformation("LDAP login for '{Username}': invalid credentials", username);
            return null;
        }

        logger.LogInformation("LDAP login for '{Username}': bound as {Dn}", username, userDn);
        return new LdapUser(userDn, displayName, memberOf);
    }

    /// <summary>
    /// Runs the configuration end to end without credentials and reports each
    /// step — used by the admin "test connection" action. The bind step doubles
    /// as the connectivity check (connection establishment is lazy on Windows).
    /// </summary>
    public LdapTestResult TestConfiguration(LdapOptions options)
    {
        var steps = new List<LdapTestStep>();

        LdapConnection? connection = null;
        try
        {
            connection = Connect(options);
            BindServiceAccount(connection, options);
            steps.Add(new LdapTestStep("bind", true,
                string.IsNullOrEmpty(options.BindDn) ? "anonymous" : options.BindDn));
        }
        catch (Exception ex)
        {
            connection?.Dispose();
            steps.Add(new LdapTestStep("bind", false, ex.Message));
            return new LdapTestResult(false, steps);
        }

        try
        {
            var request = new SearchRequest(options.BaseDn, "(objectClass=*)", SearchScope.Base, null);
            request.TimeLimit = LdapTimeout;
            var response = (SearchResponse)connection.SendRequest(request);
            steps.Add(new LdapTestStep("baseDn", true,
                $"{options.BaseDn} ({response.Entries.Count} entry)"));
        }
        catch (Exception ex)
        {
            steps.Add(new LdapTestStep("baseDn", false, ex.Message));
            connection.Dispose();
            return new LdapTestResult(false, steps);
        }
        connection.Dispose();

        var filterError = ValidateUserFilter(options.UserSearchFilter);
        steps.Add(new LdapTestStep("userFilter", filterError is null,
            filterError ?? options.UserSearchFilter));

        return new LdapTestResult(steps.All(s => s.Ok), steps);
    }

    /// <summary>The user filter must contain the {0} login-name placeholder.</summary>
    public static string? ValidateUserFilter(string? filter) =>
        string.IsNullOrWhiteSpace(filter) ? "filter is empty"
        : !filter.Contains("{0}", StringComparison.Ordinal) ? "filter must contain {0}"
        : null;

    private LdapConnection Connect(LdapOptions options)
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(options.Server, options.Port));
        connection.Timeout = LdapTimeout;
        if (options.UseSsl)
        {
            connection.SessionOptions.SecureSocketLayer = true;
            if (options.AcceptAnyCertificate)
                connection.SessionOptions.VerifyServerCertificate = (_, _) => true;
        }
        if (!options.UseSsl && options.StartTls)
        {
            connection.SessionOptions.StartTransportLayerSecurity(null);
            if (options.AcceptAnyCertificate)
                connection.SessionOptions.VerifyServerCertificate = (_, _) => true;
        }
        return connection;
    }

    private static void BindServiceAccount(LdapConnection connection, LdapOptions options)
    {
        if (!string.IsNullOrEmpty(options.BindDn))
            connection.Bind(new NetworkCredential(options.BindDn, options.BindPassword));
        else
            connection.Bind(); // anonymous search
    }

    public static string LdapFilterEscape(string value) =>
        value.Replace(@"\", @"\5c")
             .Replace("*", @"\2a")
             .Replace("(", @"\28")
             .Replace(")", @"\29")
             .Replace("\0", @"\00");
}

namespace InfinityCI.Server.Auth;

/// <summary>
/// LDAP directory authentication settings, bound from "InfinityCI:Ldap".
/// Login flow: local password first, then (when enabled) LDAP bind with
/// auto-provisioning of first-time users as plain User accounts.
/// </summary>
public sealed class LdapOptions
{
    public const string SectionName = "InfinityCI:Ldap";

    public bool Enabled { get; set; }

    /// <summary>Directory server host name or IP.</summary>
    public string Server { get; set; } = "";

    /// <summary>Directory server port; 389 for LDAP, 636 for LDAPS.</summary>
    public int Port { get; set; } = 389;

    /// <summary>Subtree root to search for user entries, e.g. "dc=example,dc=org".</summary>
    public string BaseDn { get; set; } = "";

    /// <summary>
    /// Service account DN used to search for users before binding as them.
    /// Empty means anonymous search. AD typically needs a service account;
    /// OpenLDAP often allows anonymous search.
    /// </summary>
    public string BindDn { get; set; } = "";

    public string BindPassword { get; set; } = "";

    /// <summary>
    /// Filter used to find the user entry; {0} is replaced by the login name.
    /// OpenLDAP default "(uid={0})"; Active Directory "(sAMAccountName={0})".
    /// </summary>
    public string UserSearchFilter { get; set; } = "(uid={0})";

    /// <summary>Entry attribute copied into the provisioned user's display name.</summary>
    public string DisplayNameAttribute { get; set; } = "displayName";

    /// <summary>Wrap the whole session in SSL (LDAPS); Port should be 636.</summary>
    public bool UseSsl { get; set; }

    /// <summary>Upgrade the connection with StartTLS on the plain port; ignored
    /// when <see cref="UseSsl"/> is set.</summary>
    public bool StartTls { get; set; }

    /// <summary>
    /// Skip server certificate validation for LDAPS. Only for self-signed
    /// internal directories; prefer installing the CA certificate instead.
    /// </summary>
    public bool AcceptAnyCertificate { get; set; }

    /// <summary>
    /// Group DN whose members are provisioned/synced as Admin on login; empty
    /// disables the mapping. Never grants SuperAdmin.
    /// </summary>
    public string AdminGroupDn { get; set; } = "";

    /// <summary>Project name first-time LDAP users are made visible to; empty = no projects.</summary>
    public string DefaultProject { get; set; } = "";
}

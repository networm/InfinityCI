namespace InfinityCI.Server.Auth;

/// <summary>Role constants for authorization policies.</summary>
public static class AppRoles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Admin = "Admin";
    public const string User = "User";

    /// <summary>Policy value for admins-and-above checks.</summary>
    public const string AdminsAndSuperAdmin = "Admin,SuperAdmin";
}

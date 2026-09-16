using System.Security.Claims;
using InfinityCI.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace InfinityCI.Server.Auth;

/// <summary>
/// Project visibility shared by REST endpoints and the SignalR hub: admins see
/// everything, other users only their assigned projects.
/// </summary>
public static class ProjectVisibility
{
    public static bool IsAdmin(ClaimsPrincipal user) =>
        user.IsInRole(AppRoles.SuperAdmin) || user.IsInRole(AppRoles.Admin);

    /// <summary>null = all projects visible (admins); otherwise the set of visible project names.</summary>
    public static async Task<HashSet<string>?> VisibleProjectsAsync(ClaimsPrincipal user, CiDbContext db)
    {
        if (IsAdmin(user))
            return null;
        var username = user.Identity?.Name ?? "";
        var names = await db.UserProjects
            .Where(up => up.User.Username == username)
            .Select(up => up.Project.Name)
            .ToListAsync();
        return names.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

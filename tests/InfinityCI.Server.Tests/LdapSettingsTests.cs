using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InfinityCI.Core;
using InfinityCI.Server;
using InfinityCI.Server.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfinityCI.Server.Tests;

/// <summary>Admin-managed LDAP settings: storage, permissions, group→role login
/// mapping, default-project provisioning and the connection test endpoint.</summary>
public class LdapSettingsTests : IDisposable
{
    private const string AdminGroupDn = "cn=ci-admins,dc=test";

    private readonly string _dir = TestEnv.CreateTempDir();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly StubLdapAuthenticator _ldap;

    public LdapSettingsTests()
    {
        _ldap = new StubLdapAuthenticator(AdminGroupDn);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("InfinityCI:DataDir", _dir);
            b.ConfigureServices(services =>
            {
                services.RemoveAll<LdapAuthenticator>();
                services.AddSingleton(_ldap);
                services.AddSingleton<LdapAuthenticator>(sp => sp.GetRequiredService<StubLdapAuthenticator>());
            });
        });
    }

    private HttpClient Client(string role)
    {
        var client = _factory.CreateClient();
        var credentials = role switch
        {
            "admin" => new { username = "admin", password = "admin" },
            _ => new { username = role, password = role + "-pw" },
        };
        client.PostAsJsonAsync("/api/auth/login", credentials).Wait();
        return client;
    }

    private HttpClient SuperAdmin() => Client("admin");

    private async Task<HttpClient> CreateAndLoginAsync(string username, string role)
    {
        var admin = SuperAdmin();
        (await admin.PostAsJsonAsync("/api/users", new
        {
            username,
            password = username + "-pw",
            role,
            projectIds = Array.Empty<long>(),
        })).EnsureSuccessStatusCode();
        return Client(username);
    }

    private object EnabledConfig(string? bindPassword = "s3cret", string defaultProject = "") => new
    {
        enabled = true,
        server = "ldap.corp",
        port = 389,
        baseDn = "dc=test",
        bindDn = "cn=svc,dc=test",
        bindPassword,
        userSearchFilter = "(uid={0})",
        displayNameAttribute = "displayName",
        useSsl = false,
        startTls = false,
        acceptAnyCertificate = false,
        adminGroupDn = AdminGroupDn,
        defaultProject,
    };

    [Fact]
    public async Task GetConfig_RequiresAdmin_AndPutRequiresSuperAdmin()
    {
        var viewer = await CreateAndLoginAsync("viewer", "User");
        var admin2 = await CreateAndLoginAsync("admin2", "Admin");
        var super = SuperAdmin();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.GetAsync("/api/ldap/config")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await admin2.GetAsync("/api/ldap/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin2.PutAsJsonAsync("/api/ldap/config", EnabledConfig())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin2.PostAsJsonAsync("/api/ldap/test", EnabledConfig())).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await super.PutAsJsonAsync("/api/ldap/config", EnabledConfig())).StatusCode);
    }

    [Fact]
    public async Task Save_RoundTripsFields_HidesPassword_AndReportsSource()
    {
        var super = SuperAdmin();
        (await super.PutAsJsonAsync("/api/ldap/config", new
        {
            enabled = true,
            server = "ldap.corp",
            port = 636,
            baseDn = "dc=corp",
            bindDn = "cn=svc,dc=corp",
            bindPassword = "s3cret",
            userSearchFilter = "(sAMAccountName={0})",
            displayNameAttribute = "cn",
            useSsl = true,
            startTls = false,
            acceptAnyCertificate = true,
            adminGroupDn = AdminGroupDn,
            defaultProject = "Default",
        })).EnsureSuccessStatusCode();

        var config = await (await super.GetAsync("/api/ldap/config")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ldap.corp", config.GetProperty("server").GetString());
        Assert.Equal(636, config.GetProperty("port").GetInt32());
        Assert.Equal("(sAMAccountName={0})", config.GetProperty("userSearchFilter").GetString());
        Assert.True(config.GetProperty("hasBindPassword").GetBoolean());
        Assert.Equal("db", config.GetProperty("source").GetString());
        // The bind password is write-only.
        Assert.False(config.TryGetProperty("bindPassword", out _));

        // Empty password clears the stored secret.
        (await super.PutAsJsonAsync("/api/ldap/config", EnabledConfig("", "Default")))
            .EnsureSuccessStatusCode();
        var after = await (await super.GetAsync("/api/ldap/config")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(after.GetProperty("hasBindPassword").GetBoolean());
    }

    [Fact]
    public async Task Save_RejectsInvalidConfig()
    {
        var super = SuperAdmin();
        var response = await super.PutAsJsonAsync("/api/ldap/config", new
        {
            enabled = true,
            server = "",
            port = 389,
            baseDn = "dc=corp",
            bindDn = "",
            bindPassword = "",
            userSearchFilter = "(uid={0})",
            displayNameAttribute = "displayName",
            useSsl = false,
            startTls = false,
            acceptAnyCertificate = false,
            adminGroupDn = "",
            defaultProject = "",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_ProvisionsUser_WithAdminGroupRole_AndDefaultProject()
    {
        var super = SuperAdmin();
        (await super.PutAsJsonAsync("/api/ldap/config", EnabledConfig("s3cret", "Default")))
            .EnsureSuccessStatusCode();

        // "admin-ldap" is in the admin group → provisioned as Admin with the
        // default project; "plain-ldap" is not → plain User.
        _ldap.Members["admin-ldap"] = [AdminGroupDn];
        var adminLogin = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { username = "admin-ldap", password = "ldap-pass" });
        var adminRaw = await adminLogin.Content.ReadAsStringAsync();
        System.Console.WriteLine($"ADMIN status={adminLogin.StatusCode} len={adminRaw.Length} body={adminRaw}");
        var payload = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(adminRaw);
        Assert.Equal("Admin", payload.GetProperty("role").GetString());

        var plainLogin = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { username = "plain-ldap", password = "ldap-pass" });
        var plainRaw = await plainLogin.Content.ReadAsStringAsync();
        System.Console.WriteLine($"PLAIN status={plainLogin.StatusCode} len={plainRaw.Length} body={plainRaw}");
        var plain = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(plainRaw);
        Assert.Equal("User", plain.GetProperty("role").GetString());

        var users = await (await super.GetAsync("/api/users")).Content.ReadFromJsonAsync<JsonElement>();
        var adminUser = users!.EnumerateArray().First(u => u.GetProperty("username").GetString() == "admin-ldap");
        Assert.False(adminUser.GetProperty("hasPassword").GetBoolean()); // LDAP-only account
        Assert.Contains(adminUser.GetProperty("projects").EnumerateArray(),
            p => p.GetProperty("name").GetString() == "Default");
    }

    [Fact]
    public async Task Login_SyncsRole_WhenGroupMembershipChanges()
    {
        var super = SuperAdmin();
        (await super.PutAsJsonAsync("/api/ldap/config", EnabledConfig()))
            .EnsureSuccessStatusCode();

        _ldap.Members["sync"] = [AdminGroupDn];
        (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { username = "sync", password = "ldap-pass" })).EnsureSuccessStatusCode();
        var users = await (await super.GetAsync("/api/users")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Admin", users!.EnumerateArray()
            .First(u => u.GetProperty("username").GetString() == "sync")
            .GetProperty("role").GetString());

        // Removed from the group → synced back down to User on next login.
        _ldap.Members["sync"] = [];
        (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { username = "sync", password = "ldap-pass" })).EnsureSuccessStatusCode();
        users = await (await super.GetAsync("/api/users")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("User", users!.EnumerateArray()
            .First(u => u.GetProperty("username").GetString() == "sync")
            .GetProperty("role").GetString());
    }

    [Fact]
    public async Task DisabledLdap_Login_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "anyone", password = "ldap-pass" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestConnection_ReportsUnreachableServer()
    {
        var super = SuperAdmin();
        var config = new
        {
            enabled = true,
            server = "127.0.0.1",
            port = 1, // nothing listens here
            baseDn = "dc=test",
            bindDn = "",
            bindPassword = "",
            userSearchFilter = "(uid={0})",
            displayNameAttribute = "displayName",
            useSsl = false,
            startTls = false,
            acceptAnyCertificate = false,
            adminGroupDn = "",
            defaultProject = "",
        };
        (await super.PutAsJsonAsync("/api/ldap/config", config)).EnsureSuccessStatusCode();

        var response = await super.PostAsJsonAsync("/api/ldap/test", config);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("ok").GetBoolean()); // server unreachable
        var steps = payload.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal("bind", steps[0].GetProperty("name").GetString());
        Assert.False(steps[0].GetProperty("ok").GetBoolean());
    }

    public void Dispose()
    {
        try
        {
            _factory.Dispose();
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }

    /// <summary>Authentication stub: password "ldap-pass" always binds; each
    /// username maps to its configured memberOf list.</summary>
    private sealed class StubLdapAuthenticator(string adminGroupDn)
        : LdapAuthenticator(NullLogger<LdapAuthenticator>.Instance)
    {
        public Dictionary<string, string[]> Members { get; } = new(StringComparer.Ordinal);

        public override LdapUser? Authenticate(LdapOptions options, string username, string password)
        {
            if (!options.Enabled || password != "ldap-pass")
                return null;
            return new LdapUser($"cn={username},dc=test", username,
                Members.GetValueOrDefault(username, []));
        }
    }
}

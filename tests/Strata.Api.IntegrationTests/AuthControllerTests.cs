using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Strata.Api.IntegrationTests;

public class AuthControllerTests : IntegrationTestBase
{
    public AuthControllerTests(IntegrationTestFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Register_creates_tenant_and_assigns_user_to_it()
    {
        var client = Fixture.Factory.CreateClient();
        var beforeCall = DateTimeOffset.UtcNow;

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { Email = "tenant-register-a@test.local", Password = "P@ssw0rd123!", TenantName = "  Acme Corp  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var userId = TestApiHelpers.UserIdFromToken(json.GetProperty("token").GetString()!);

        var tenants = await Fixture.QueryDbAsync(db => db.Tenants.AsNoTracking().ToListAsync());
        var tenant = Assert.Single(tenants);

        Assert.Equal("Acme Corp", tenant.Name);
        Assert.True(tenant.CreatedAt >= beforeCall);

        var user = await Fixture.QueryDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == userId));
        Assert.Equal(tenant.Id, user.TenantId);
    }

    [Fact]
    public async Task Register_with_invalid_password_creates_no_tenant_or_user()
    {
        var client = Fixture.Factory.CreateClient();
        var tenantCountBefore = await Fixture.QueryDbAsync(db => db.Tenants.AsNoTracking().CountAsync());

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { Email = "tenant-badpassword@test.local", Password = "weak", TenantName = "Some Tenant" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var tenantCountAfter = await Fixture.QueryDbAsync(db => db.Tenants.AsNoTracking().CountAsync());
        Assert.Equal(tenantCountBefore, tenantCountAfter);

        var userExists = await Fixture.QueryDbAsync(db =>
            db.Users.AsNoTracking().AnyAsync(u => u.Email == "tenant-badpassword@test.local"));
        Assert.False(userExists);
    }

    [Fact]
    public async Task Register_with_duplicate_email_creates_no_additional_tenant()
    {
        var client = Fixture.Factory.CreateClient();
        await TestApiHelpers.RegisterAsync(client, "tenant-dup@test.local", tenantName: "First Tenant");

        var tenantCountBefore = await Fixture.QueryDbAsync(db => db.Tenants.AsNoTracking().CountAsync());

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { Email = "tenant-dup@test.local", Password = "P@ssw0rd123!", TenantName = "Second Tenant" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var tenantCountAfter = await Fixture.QueryDbAsync(db => db.Tenants.AsNoTracking().CountAsync());
        Assert.Equal(tenantCountBefore, tenantCountAfter);
    }

    [Fact]
    public async Task Register_with_missing_tenant_name_returns_400()
    {
        var client = Fixture.Factory.CreateClient();
        var tenantCountBefore = await Fixture.QueryDbAsync(db => db.Tenants.AsNoTracking().CountAsync());

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { Email = "tenant-missing-name@test.local", Password = "P@ssw0rd123!" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var tenantCountAfter = await Fixture.QueryDbAsync(db => db.Tenants.AsNoTracking().CountAsync());
        Assert.Equal(tenantCountBefore, tenantCountAfter);

        var userExists = await Fixture.QueryDbAsync(db =>
            db.Users.AsNoTracking().AnyAsync(u => u.Email == "tenant-missing-name@test.local"));
        Assert.False(userExists);
    }

    [Fact]
    public async Task Register_with_blank_tenant_name_returns_400()
    {
        var client = Fixture.Factory.CreateClient();
        var tenantCountBefore = await Fixture.QueryDbAsync(db => db.Tenants.AsNoTracking().CountAsync());

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { Email = "tenant-blank-name@test.local", Password = "P@ssw0rd123!", TenantName = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var tenantCountAfter = await Fixture.QueryDbAsync(db => db.Tenants.AsNoTracking().CountAsync());
        Assert.Equal(tenantCountBefore, tenantCountAfter);
    }

    [Fact]
    public async Task Register_with_overlong_tenant_name_returns_400()
    {
        var client = Fixture.Factory.CreateClient();
        var tenantCountBefore = await Fixture.QueryDbAsync(db => db.Tenants.AsNoTracking().CountAsync());

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "tenant-overlong-name@test.local",
            Password = "P@ssw0rd123!",
            TenantName = new string('a', 201)
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var tenantCountAfter = await Fixture.QueryDbAsync(db => db.Tenants.AsNoTracking().CountAsync());
        Assert.Equal(tenantCountBefore, tenantCountAfter);
    }

    [Fact]
    public async Task Register_ignores_client_supplied_tenant_id()
    {
        var client = Fixture.Factory.CreateClient();
        var craftedTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        // RegisterRequest has no TenantId property at all — a crafted extra
        // JSON field must not bind to anything or influence the stored value.
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = "tenant-crafted-id@test.local",
            Password = "P@ssw0rd123!",
            TenantName = "Crafted Tenant",
            tenantId = craftedTenantId
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var userId = TestApiHelpers.UserIdFromToken(json.GetProperty("token").GetString()!);

        var user = await Fixture.QueryDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == userId));
        Assert.NotEqual(craftedTenantId, user.TenantId);

        var tenantExists = await Fixture.QueryDbAsync(db => db.Tenants.AsNoTracking().AnyAsync(t => t.Id == user.TenantId));
        Assert.True(tenantExists);
    }
}

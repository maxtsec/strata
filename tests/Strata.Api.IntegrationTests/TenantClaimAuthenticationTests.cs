using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Strata.Application.Tenancy;

namespace Strata.Api.IntegrationTests;

// Proves tenant_id validation happens at the JWT bearer authentication
// boundary itself — not only inside ICurrentTenant, which nothing in this
// PR's scope actually consumes. Every token here is signed with the real
// test signing key and carries a normal expiry, so it passes signature and
// lifetime validation; only the tenant_id claim shape varies. These are
// real HTTP calls against GET /api/folders, not direct construction of
// HttpContextCurrentTenant.
public class TenantClaimAuthenticationTests : IntegrationTestBase
{
    public TenantClaimAuthenticationTests(IntegrationTestFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Token_without_tenant_id_returns_401()
    {
        var response = await SendWithClaims(new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_with_malformed_tenant_id_returns_401()
    {
        var response = await SendWithClaims(
            new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new Claim(TenantClaimTypes.TenantId, "not-a-guid"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_with_guid_empty_tenant_id_returns_401()
    {
        var response = await SendWithClaims(
            new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new Claim(TenantClaimTypes.TenantId, Guid.Empty.ToString()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_with_duplicate_tenant_id_claims_returns_401()
    {
        var response = await SendWithClaims(
            new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new Claim(TenantClaimTypes.TenantId, Guid.NewGuid().ToString()),
            new Claim(TenantClaimTypes.TenantId, Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_with_one_valid_tenant_id_succeeds()
    {
        var client = await TestApiHelpers.AuthenticatedClientAsync(Fixture.Factory, "tenant-claim-auth@test.local");

        var response = await client.GetAsync("/api/folders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendWithClaims(params Claim[] claims)
    {
        var client = Fixture.Factory.CreateClient();
        var token = TestApiHelpers.CreateToken(claims);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.GetAsync("/api/folders");
    }
}

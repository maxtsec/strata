using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Strata.Api.Tenancy;
using Strata.Application.Tenancy;

namespace Strata.Api.IntegrationTests;

public class HttpContextCurrentTenantTests
{
    [Fact]
    public void Resolves_tenant_id_from_a_valid_claim()
    {
        var tenantId = Guid.NewGuid();
        var accessor = AccessorWithClaims(new Claim(TenantClaimTypes.TenantId, tenantId.ToString()));

        var currentTenant = new HttpContextCurrentTenant(accessor);

        Assert.Equal(tenantId, currentTenant.TenantId);
    }

    [Fact]
    public void Throws_when_the_claim_is_missing()
    {
        var accessor = AccessorWithClaims();

        Assert.Throws<InvalidOperationException>(() => new HttpContextCurrentTenant(accessor));
    }

    [Fact]
    public void Throws_when_the_claim_is_not_a_valid_guid()
    {
        var accessor = AccessorWithClaims(new Claim(TenantClaimTypes.TenantId, "not-a-guid"));

        Assert.Throws<InvalidOperationException>(() => new HttpContextCurrentTenant(accessor));
    }

    [Fact]
    public void Throws_when_the_claim_is_guid_empty()
    {
        var accessor = AccessorWithClaims(new Claim(TenantClaimTypes.TenantId, Guid.Empty.ToString()));

        Assert.Throws<InvalidOperationException>(() => new HttpContextCurrentTenant(accessor));
    }

    [Fact]
    public void Throws_when_there_are_duplicate_tenant_id_claims()
    {
        var accessor = AccessorWithClaims(
            new Claim(TenantClaimTypes.TenantId, Guid.NewGuid().ToString()),
            new Claim(TenantClaimTypes.TenantId, Guid.NewGuid().ToString()));

        Assert.Throws<InvalidOperationException>(() => new HttpContextCurrentTenant(accessor));
    }

    [Fact]
    public void Throws_when_there_is_no_http_context_at_all()
    {
        var accessor = new HttpContextAccessor { HttpContext = null };

        Assert.Throws<InvalidOperationException>(() => new HttpContextCurrentTenant(accessor));
    }

    private static IHttpContextAccessor AccessorWithClaims(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, authenticationType: "TestAuth");
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };

        return new HttpContextAccessor { HttpContext = httpContext };
    }
}

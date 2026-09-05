using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Strata.Api.Tenancy;
using Strata.Infrastructure.Identity;

namespace Strata.Api.IntegrationTests;

public class HttpContextCurrentTenantTests
{
    [Fact]
    public void Resolves_tenant_id_from_a_valid_claim()
    {
        var tenantId = Guid.NewGuid();
        var accessor = AccessorWithClaim(JwtTokenGenerator.TenantIdClaimType, tenantId.ToString());

        var currentTenant = new HttpContextCurrentTenant(accessor);

        Assert.Equal(tenantId, currentTenant.TenantId);
    }

    [Fact]
    public void Throws_when_the_claim_is_missing()
    {
        var accessor = AccessorWithClaim(claimType: null, claimValue: null);

        Assert.Throws<InvalidOperationException>(() => new HttpContextCurrentTenant(accessor));
    }

    [Fact]
    public void Throws_when_the_claim_is_not_a_valid_guid()
    {
        var accessor = AccessorWithClaim(JwtTokenGenerator.TenantIdClaimType, "not-a-guid");

        Assert.Throws<InvalidOperationException>(() => new HttpContextCurrentTenant(accessor));
    }

    [Fact]
    public void Throws_when_there_is_no_http_context_at_all()
    {
        var accessor = new HttpContextAccessor { HttpContext = null };

        Assert.Throws<InvalidOperationException>(() => new HttpContextCurrentTenant(accessor));
    }

    private static IHttpContextAccessor AccessorWithClaim(string? claimType, string? claimValue)
    {
        var claims = claimType is null
            ? []
            : new[] { new Claim(claimType, claimValue!) };

        var identity = new ClaimsIdentity(claims, authenticationType: "TestAuth");
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };

        return new HttpContextAccessor { HttpContext = httpContext };
    }
}

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
        var currentTenant = new HttpContextCurrentTenant(accessor);

        Assert.Throws<InvalidOperationException>(() => currentTenant.TenantId);
    }

    [Fact]
    public void Throws_when_the_claim_is_not_a_valid_guid()
    {
        var accessor = AccessorWithClaims(new Claim(TenantClaimTypes.TenantId, "not-a-guid"));
        var currentTenant = new HttpContextCurrentTenant(accessor);

        Assert.Throws<InvalidOperationException>(() => currentTenant.TenantId);
    }

    [Fact]
    public void Throws_when_the_claim_is_guid_empty()
    {
        var accessor = AccessorWithClaims(new Claim(TenantClaimTypes.TenantId, Guid.Empty.ToString()));
        var currentTenant = new HttpContextCurrentTenant(accessor);

        Assert.Throws<InvalidOperationException>(() => currentTenant.TenantId);
    }

    [Fact]
    public void Throws_when_there_are_duplicate_tenant_id_claims()
    {
        var accessor = AccessorWithClaims(
            new Claim(TenantClaimTypes.TenantId, Guid.NewGuid().ToString()),
            new Claim(TenantClaimTypes.TenantId, Guid.NewGuid().ToString()));
        var currentTenant = new HttpContextCurrentTenant(accessor);

        Assert.Throws<InvalidOperationException>(() => currentTenant.TenantId);
    }

    [Fact]
    public void Throws_when_there_is_no_http_context_at_all()
    {
        var accessor = new HttpContextAccessor { HttpContext = null };
        var currentTenant = new HttpContextCurrentTenant(accessor);

        Assert.Throws<InvalidOperationException>(() => currentTenant.TenantId);
    }

    [Fact]
    public void Construction_never_throws_regardless_of_context()
    {
        // AppDbContext takes an ICurrentTenant in its constructor and must be
        // constructible outside an HTTP request (migrations, test setup) —
        // so construction itself must never touch the tenant claim.
        var accessor = new HttpContextAccessor { HttpContext = null };

        var exception = Record.Exception(() => new HttpContextCurrentTenant(accessor));

        Assert.Null(exception);
    }

    [Fact]
    public void IsAvailable_is_false_when_there_is_no_http_context_at_all()
    {
        var accessor = new HttpContextAccessor { HttpContext = null };
        var currentTenant = new HttpContextCurrentTenant(accessor);

        Assert.False(currentTenant.IsAvailable);
    }

    [Fact]
    public void IsAvailable_is_true_even_when_the_claim_inside_is_invalid()
    {
        // A real request always has an HttpContext, valid claim or not —
        // IsAvailable answers "is there a request", not "is the claim ok".
        var accessor = AccessorWithClaims();
        var currentTenant = new HttpContextCurrentTenant(accessor);

        Assert.True(currentTenant.IsAvailable);
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

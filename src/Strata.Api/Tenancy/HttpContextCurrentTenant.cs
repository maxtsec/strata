using Strata.Application.Tenancy;
using Strata.Infrastructure.Identity;

namespace Strata.Api.Tenancy;

public class HttpContextCurrentTenant : ICurrentTenant
{
    public Guid TenantId { get; }

    public HttpContextCurrentTenant(IHttpContextAccessor httpContextAccessor)
    {
        var claim = httpContextAccessor.HttpContext?.User.FindFirst(JwtTokenGenerator.TenantIdClaimType)?.Value;

        if (!Guid.TryParse(claim, out var tenantId))
        {
            throw new InvalidOperationException(
                "The current request has no valid tenant_id claim. Every authenticated " +
                "user has a required TenantId, so a missing or malformed claim here means " +
                "either a bug in token issuance or a tampered token — not a case to default around.");
        }

        TenantId = tenantId;
    }
}

using Strata.Application.Tenancy;

namespace Strata.Api.Tenancy;

public class HttpContextCurrentTenant : ICurrentTenant
{
    public Guid TenantId { get; }

    public HttpContextCurrentTenant(IHttpContextAccessor httpContextAccessor)
    {
        var claims = httpContextAccessor.HttpContext?.User.Claims ?? [];

        if (!TenantClaimTypes.TryGetValidTenantId(claims, out var tenantId))
        {
            throw new InvalidOperationException(
                "The current request has no valid tenant_id claim — either missing, " +
                "not a Guid, Guid.Empty, or present more than once. The JWT bearer " +
                "authentication boundary should already reject such tokens with 401, " +
                "so reaching this constructor with an invalid claim means either a bug " +
                "in that validation or a tampered token — not a case to default around.");
        }

        TenantId = tenantId;
    }
}

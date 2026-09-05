using System.Security.Claims;

namespace Strata.Application.Tenancy;

// Lives in Application (not Infrastructure or Api) so both the JWT issuer
// (Infrastructure) and the two places that enforce it — the JWT bearer
// authentication boundary and ICurrentTenant (both Api) — share one
// definition of what a valid tenant claim looks like, instead of each
// re-deriving the rule.
public static class TenantClaimTypes
{
    public const string TenantId = "tenant_id";

    // Exactly one tenant_id claim, parseable as a Guid, not Guid.Empty.
    // Never picks the first of several — that would silently accept a
    // token carrying more than one tenant claim.
    public static bool TryGetValidTenantId(IEnumerable<Claim> claims, out Guid tenantId)
    {
        var matches = claims.Where(c => c.Type == TenantId).ToList();

        if (matches.Count == 1 && Guid.TryParse(matches[0].Value, out var parsed) && parsed != Guid.Empty)
        {
            tenantId = parsed;
            return true;
        }

        tenantId = Guid.Empty;
        return false;
    }
}

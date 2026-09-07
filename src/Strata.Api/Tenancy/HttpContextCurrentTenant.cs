using Strata.Application.Tenancy;

namespace Strata.Api.Tenancy;

public class HttpContextCurrentTenant : ICurrentTenant
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextCurrentTenant(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public bool IsAvailable => _httpContextAccessor.HttpContext is not null;

    // Resolved lazily, on access, rather than in the constructor: AppDbContext
    // now takes an ICurrentTenant too, and it must be constructible outside an
    // HTTP request (migrations, test setup) without a valid tenant to resolve.
    // Fail-closed still holds — it just moves to the moment the tenant identity
    // is actually read, instead of the moment this object is created.
    public Guid TenantId
    {
        get
        {
            var claims = _httpContextAccessor.HttpContext?.User.Claims ?? [];

            if (!TenantClaimTypes.TryGetValidTenantId(claims, out var tenantId))
            {
                throw new InvalidOperationException(
                    "The current request has no valid tenant_id claim — either missing, " +
                    "not a Guid, Guid.Empty, or present more than once. The JWT bearer " +
                    "authentication boundary should already reject such tokens with 401, " +
                    "so reaching here with an invalid claim means either a bug in that " +
                    "validation or a tampered token — not a case to default around.");
            }

            return tenantId;
        }
    }
}

namespace Strata.Application.Tenancy;

// The trusted, request-scoped answer to "which tenant is this request acting
// as". Resolved once per request from a validated JWT claim — never from a
// request body or query string. Implementations must fail closed: if the
// current request has no valid tenant, that is a bug or a tampered token,
// not a case to paper over with a default.
public interface ICurrentTenant
{
    Guid TenantId { get; }

    // Whether a request-scoped tenant context exists at all — distinct from
    // whether TenantId is valid. False outside any request (migrations,
    // test/admin seeding): there is no "current tenant" question to ask.
    // True but TenantId still throwing means a request reached here with an
    // invalid claim — a bug or a tampered token, never a case to skip.
    bool IsAvailable { get; }
}

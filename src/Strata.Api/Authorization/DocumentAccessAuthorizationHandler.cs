using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Strata.Application.Persistence;
using Strata.Domain.Documents;

namespace Strata.Api.Authorization;

// Document-specific: grants access to the owner or anyone the document has
// been shared with. Unlike OwnerAuthorizationHandler this needs the
// database, so it can't be a pure, Singleton-registered check — it's
// registered Scoped, alongside the DbContext it depends on.
public class DocumentAccessAuthorizationHandler : AuthorizationHandler<DocumentAccessRequirement, Document>
{
    private readonly IApplicationDbContext _dbContext;

    public DocumentAccessAuthorizationHandler(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        DocumentAccessRequirement requirement,
        Document resource)
    {
        var userIdClaim = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return;
        }

        if (userId == resource.OwnerId)
        {
            context.Succeed(requirement);
            return;
        }

        var isShared = await _dbContext.DocumentShares
            .AnyAsync(share => share.DocumentId == resource.Id && share.UserId == userId);

        if (isShared)
        {
            context.Succeed(requirement);
        }
    }
}

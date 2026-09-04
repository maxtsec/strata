using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Strata.Application.Persistence;
using Strata.Domain.Documents;

namespace Strata.Api.Authorization;

// Narrower than DocumentAccessRequirement: the owner or a Member share can
// edit, but a Viewer share (read-only by design) cannot. Scoped for the
// same reason as DocumentAccessAuthorizationHandler — it needs the
// DbContext, so it can't be the pure, Singleton-safe kind of handler.
public class DocumentEditAuthorizationHandler : AuthorizationHandler<DocumentEditRequirement, Document>
{
    private readonly IApplicationDbContext _dbContext;

    public DocumentEditAuthorizationHandler(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        DocumentEditRequirement requirement,
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

        var canEdit = await _dbContext.DocumentShares.AnyAsync(share =>
            share.DocumentId == resource.Id &&
            share.UserId == userId &&
            share.UserRole == DocumentShare.Role.Member);

        if (canEdit)
        {
            context.Succeed(requirement);
        }
    }
}

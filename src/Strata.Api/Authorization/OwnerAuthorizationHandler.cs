using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Strata.Domain;

namespace Strata.Api.Authorization;

public class OwnerAuthorizationHandler : AuthorizationHandler<OwnerRequirement, IOwnable>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OwnerRequirement requirement,
        IOwnable resource)
    {
        var userIdClaim = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        if (userIdClaim is not null && Guid.Parse(userIdClaim) == resource.OwnerId)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
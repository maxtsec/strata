using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Strata.Api.Authorization;
using Strata.Domain;

namespace Strata.Api.IntegrationTests;

// Pure unit tests — no DB, no HTTP host. Exercises OwnerAuthorizationHandler
// directly via the public IAuthorizationHandler.HandleAsync entry point (the
// protected HandleRequirementAsync it overrides isn't callable from outside).
public class OwnerAuthorizationHandlerTests
{
    private class FakeOwnable : IOwnable
    {
        public required Guid OwnerId { get; init; }
    }

    private static AuthorizationHandlerContext BuildContext(string? sub, IOwnable resource)
    {
        var claims = sub is null
            ? Array.Empty<Claim>()
            : new[] { new Claim(JwtRegisteredClaimNames.Sub, sub) };
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        return new AuthorizationHandlerContext(new[] { new OwnerRequirement() }, user, resource);
    }

    [Fact]
    public async Task Succeeds_when_sub_matches_owner()
    {
        var ownerId = Guid.NewGuid();
        var context = BuildContext(ownerId.ToString(), new FakeOwnable { OwnerId = ownerId });

        await new OwnerAuthorizationHandler().HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Fails_when_sub_does_not_match_owner()
    {
        var context = BuildContext(Guid.NewGuid().ToString(), new FakeOwnable { OwnerId = Guid.NewGuid() });

        await new OwnerAuthorizationHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Fails_closed_when_sub_is_malformed()
    {
        var context = BuildContext("not-a-guid", new FakeOwnable { OwnerId = Guid.NewGuid() });

        await new OwnerAuthorizationHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Fails_closed_when_sub_is_missing()
    {
        var context = BuildContext(null, new FakeOwnable { OwnerId = Guid.NewGuid() });

        await new OwnerAuthorizationHandler().HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }
}

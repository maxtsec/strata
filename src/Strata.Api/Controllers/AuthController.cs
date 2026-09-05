using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Strata.Application.Persistence;
using Strata.Domain.Tenancy;
using Strata.Infrastructure.Identity;

namespace Strata.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly JwtTokenGenerator _jwtTokenGenerator;
    private readonly IApplicationDbContext _dbContext;
    public AuthController(UserManager<ApplicationUser> userManager, JwtTokenGenerator jwtTokenGenerator, IApplicationDbContext dbContext)
    {
        _userManager = userManager;
        _jwtTokenGenerator = jwtTokenGenerator;
        _dbContext = dbContext;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TenantName))
        {
            return BadRequest("Tenant name is required.");
        }

        var tenantName = request.TenantName.Trim();
        if (tenantName.Length > 200)
        {
            return BadRequest("Tenant name must be 200 characters or fewer.");
        }

        // Tracked as Added here, not yet persisted. UserManager.CreateAsync
        // below runs all its own validation (duplicate email, password
        // strength) before it ever calls SaveChangesAsync on this same
        // DbContext — so a validation failure never reaches SaveChanges at
        // all, and this Tenant is never written. When it does succeed,
        // Identity's own SaveChangesAsync call flushes everything still
        // tracked on this context in one transaction, this Tenant included,
        // making Tenant + user creation atomic without a manual transaction.
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = tenantName,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _dbContext.Tenants.Add(tenant);

        var user = new ApplicationUser
        {
            Email = request.Email,
            UserName = request.Email,
            TenantId = tenant.Id
      };

        var result = await _userManager.CreateAsync(user, request.Password);

        if (!result.Succeeded)
        {
            return BadRequest(result.Errors);
        }

        var roles = await _userManager.GetRolesAsync(user);

        var token = _jwtTokenGenerator.GenerateToken(user, roles);

        return Ok(new { Token = token });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);

        if (user == null || !await _userManager.CheckPasswordAsync(user, request.Password))
        {
            return Unauthorized();
        }

        var roles = await _userManager.GetRolesAsync(user);

        var token = _jwtTokenGenerator.GenerateToken(user, roles);

        return Ok(new { Token = token });
    }

}

public record RegisterRequest(string Email, string Password, string TenantName);
public record LoginRequest(string Email, string Password);
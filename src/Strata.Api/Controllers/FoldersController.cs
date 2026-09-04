// Strata.Api/Controllers/FoldersController.cs
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Strata.Api.Authorization;
using Strata.Application.Persistence;
using Strata.Domain.Documents;

namespace Strata.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class FoldersController : ControllerBase
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IAuthorizationService _authorizationService;

    public FoldersController(IApplicationDbContext dbContext, IAuthorizationService authorizationService)
    {
        _dbContext = dbContext;
        _authorizationService = authorizationService;
    }

    private Guid CurrentUserId =>
        Guid.Parse(User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value);

    [HttpPost]
    public async Task<IActionResult> Create(CreateFolderRequest request, CancellationToken cancellationToken)
    {
        if (request.ParentFolderId is { } parentId && !await IsOwnedByCurrentUser(parentId, cancellationToken))
        {
            return BadRequest("Parent folder not found.");
        }

        var folder = new Folder
        {
            Id = Guid.NewGuid(),
            OwnerId = CurrentUserId,
            ParentFolderId = request.ParentFolderId,
            Name = request.Name
        };

        _dbContext.Folders.Add(folder);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { FolderId = folder.Id });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var folders = await _dbContext.Folders
            .Where(f => f.OwnerId == CurrentUserId)
            .ToListAsync(cancellationToken);

        return Ok(folders);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, UpdateFolderRequest request, CancellationToken cancellationToken)
    {
        var folder = await _dbContext.Folders.FindAsync(new object[] { id }, cancellationToken);
        if (folder is null)
        {
            return NotFound();
        }

        var authResult = await _authorizationService.AuthorizeAsync(User, folder, new OwnerRequirement());
        if (!authResult.Succeeded)
        {
            return Forbid();
        }

        if (request.ParentFolderId is { } parentId)
        {
            if (parentId == id)
            {
                return BadRequest("A folder cannot be its own parent.");
            }

            if (!await IsOwnedByCurrentUser(parentId, cancellationToken))
            {
                return BadRequest("Parent folder not found.");
            }
        }

        folder.Name = request.Name;
        folder.ParentFolderId = request.ParentFolderId;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var folder = await _dbContext.Folders.FindAsync(new object[] { id }, cancellationToken);
        if (folder is null)
        {
            return NotFound();
        }

        var authResult = await _authorizationService.AuthorizeAsync(User, folder, new OwnerRequirement());
        if (!authResult.Succeeded)
        {
            return Forbid();
        }

        _dbContext.Folders.Remove(folder);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    private async Task<bool> IsOwnedByCurrentUser(Guid folderId, CancellationToken cancellationToken)
    {
        var folder = await _dbContext.Folders.FindAsync(new object[] { folderId }, cancellationToken);
        if (folder is null)
        {
            return false;
        }

        var authResult = await _authorizationService.AuthorizeAsync(User, folder, new OwnerRequirement());
        return authResult.Succeeded;
    }
}

public record CreateFolderRequest(string Name, Guid? ParentFolderId);

public record UpdateFolderRequest(string Name, Guid? ParentFolderId);

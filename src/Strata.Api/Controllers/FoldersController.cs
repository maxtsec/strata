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

    private Guid? CurrentUserId =>
        Guid.TryParse(User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var userId) ? userId : null;

    [HttpPost]
    public async Task<IActionResult> Create(CreateFolderRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId is not { } userId)
        {
            return Unauthorized();
        }

        if (request.ParentFolderId is { } parentId && !await IsOwnedByCurrentUser(parentId, cancellationToken))
        {
            return BadRequest("Parent folder not found.");
        }

        var folder = new Folder
        {
            Id = Guid.NewGuid(),
            OwnerId = userId,
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
        if (CurrentUserId is not { } userId)
        {
            return Unauthorized();
        }

        var folders = await _dbContext.Folders
            .Where(f => f.OwnerId == userId)
            .ToListAsync(cancellationToken);

        return Ok(folders);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(Guid id, UpdateFolderRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId is not { } userId)
        {
            return Unauthorized();
        }

        var folder = await _dbContext.Folders.FindAsync(new object[] { id }, cancellationToken);
        if (folder is null)
        {
            return NotFound();
        }

        var authResult = await _authorizationService.AuthorizeAsync(User, folder, new OwnerRequirement());
        if (!authResult.Succeeded)
        {
            // Missing vs. not-owned both resolve to 404 (anti-enumeration).
            return NotFound();
        }

        if (request.ParentFolderId is { } parentId)
        {
            // Fetch the whole tree once: confirms parent ownership (membership in
            // this dictionary implies OwnerId == userId) and lets the cycle walk
            // below run entirely in memory, in one round trip.
            var ownFolders = await _dbContext.Folders
                .Where(f => f.OwnerId == userId)
                .ToListAsync(cancellationToken);
            var parentOf = ownFolders.ToDictionary(f => f.Id, f => f.ParentFolderId);

            if (!parentOf.ContainsKey(parentId))
            {
                return BadRequest("Parent folder not found.");
            }

            // Walk from the proposed parent up toward the root looking for `id`.
            // A visited set bounds the walk even if pre-existing data already has
            // an unrelated cycle in it — we only care whether *this* folder would
            // become its own ancestor, not whether the rest of the tree is clean.
            var visited = new HashSet<Guid>();
            var current = (Guid?)parentId;
            while (current is { } currentId)
            {
                if (currentId == id)
                {
                    return BadRequest("This move would create a folder cycle.");
                }

                if (!visited.Add(currentId))
                {
                    break;
                }

                current = parentOf.TryGetValue(currentId, out var next) ? next : null;
            }

            // Note: two concurrent requests moving folders in opposite directions
            // (A→B and B→A at the same time) can each pass this check against a
            // stale snapshot and still produce a cycle once both commit. Accepted
            // as a known race — the affected data is scoped to one user's own
            // tree, not a cross-user boundary, so it isn't a security issue, and
            // isn't worth transaction/locking machinery at this scale.
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
            return NotFound();
        }

        var hasChildFolders = await _dbContext.Folders.AnyAsync(f => f.ParentFolderId == id, cancellationToken);
        var hasDocuments = await _dbContext.Documents.AnyAsync(d => d.FolderId == id, cancellationToken);
        if (hasChildFolders || hasDocuments)
        {
            return Conflict("Folder is not empty.");
        }

        _dbContext.Folders.Remove(folder);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The pre-check above covers the common case; this catches the rare
            // race where a child folder/document was inserted concurrently, after
            // the check but before this save. The FK constraint (DeleteBehavior
            // .Restrict) is what actually guarantees data integrity either way —
            // this only turns that into a clean 409 instead of an unhandled 500.
            return Conflict("Folder is not empty.");
        }

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

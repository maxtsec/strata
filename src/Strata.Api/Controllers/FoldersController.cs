// Strata.Api/Controllers/FoldersController.cs
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Strata.Api.Authorization;
using Strata.Application.Persistence;
using Strata.Application.Tenancy;
using Strata.Domain.Documents;

namespace Strata.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class FoldersController : ControllerBase
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IAuthorizationService _authorizationService;
    private readonly ICurrentTenant _currentTenant;

    public FoldersController(IApplicationDbContext dbContext, IAuthorizationService authorizationService, ICurrentTenant currentTenant)
    {
        _dbContext = dbContext;
        _authorizationService = authorizationService;
        _currentTenant = currentTenant;
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
            TenantId = _currentTenant.TenantId,
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
            // Fail closed: a repeated node (pre-existing corrupt data) or an
            // ancestor missing from this user's own folder map (a foreign or
            // dangling reference) both abort the move rather than silently
            // allowing it — only a *genuine* null parent counts as a clean path
            // to the root.
            var visited = new HashSet<Guid>();
            var current = (Guid?)parentId;
            while (current is { } currentId)
            {
                if (currentId == id || !visited.Add(currentId))
                {
                    return BadRequest("This move would create a folder cycle.");
                }

                if (!parentOf.TryGetValue(currentId, out var next))
                {
                    return BadRequest("This move would create a folder cycle.");
                }

                current = next;
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
            // the check but before this save. Re-verify rather than assume the FK
            // constraint is why it failed — some other DbUpdateException (a
            // transient connection issue, an unrelated constraint) would otherwise
            // get misreported as "not empty".
            var stillHasChildFolders = await _dbContext.Folders.AnyAsync(f => f.ParentFolderId == id, cancellationToken);
            var stillHasDocuments = await _dbContext.Documents.AnyAsync(d => d.FolderId == id, cancellationToken);
            if (stillHasChildFolders || stillHasDocuments)
            {
                return Conflict("Folder is not empty.");
            }

            throw;
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

// Strata.Api/Controllers/DocumentsController.cs
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Strata.Api.Authorization;
using Strata.Application.Persistence;
using Strata.Application.Tenancy;
using Strata.Domain.Documents;
using Strata.Infrastructure.Identity;

namespace Strata.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DocumentsController : ControllerBase
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IFileStorage _fileStorage;
    private readonly IAuthorizationService _authorizationService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ICurrentTenant _currentTenant;

    public DocumentsController(
        IApplicationDbContext dbContext,
        IFileStorage fileStorage,
        IAuthorizationService authorizationService,
        UserManager<ApplicationUser> userManager,
        ICurrentTenant currentTenant)
    {
        _dbContext = dbContext;
        _fileStorage = fileStorage;
        _authorizationService = authorizationService;
        _userManager = userManager;
        _currentTenant = currentTenant;
    }

    private Guid? CurrentUserId =>
        Guid.TryParse(User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var userId) ? userId : null;

    [HttpPost]
    public async Task<IActionResult> Create(CreateDocumentRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId is not { } userId)
        {
            return Unauthorized();
        }

        if (request.FolderId is { } folderId && !await IsFolderOwnedByCurrentUser(folderId, cancellationToken))
        {
            return BadRequest("Folder not found.");
        }

        var document = new Document
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            OwnerId = userId,
            TenantId = _currentTenant.TenantId,
            FolderId = request.FolderId,
            ContentType = request.ContentType,
            Size = request.Size
        };

        _dbContext.Documents.Add(document);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var uploadUri = await _fileStorage.GetUploadUriAsync(document.Id, request.ContentType, cancellationToken);

        return Ok(new { DocumentId = document.Id, UploadUrl = uploadUri });
    }

    [HttpGet("{id}/download")]
    public async Task<IActionResult> GetDownloadUrl(Guid id, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.FindAsync(new object[] { id }, cancellationToken);

        if (document == null)
        {
            return NotFound();
        }

        var authResult = await _authorizationService.AuthorizeAsync(User, document, new DocumentAccessRequirement());
        if (!authResult.Succeeded)
        {
            // Missing vs. not-owned/not-shared both resolve to 404, so a response
            // can't be used to tell whether an id exists at all (anti-enumeration).
            return NotFound();
        }

        var downloadUri = await _fileStorage.GetDownloadUriAsync(id, cancellationToken);
        return Ok(new { DownloadUrl = downloadUri });
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Rename(Guid id, UpdateDocumentRequest request, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.FindAsync(new object[] { id }, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        var authResult = await _authorizationService.AuthorizeAsync(User, document, new DocumentEditRequirement());
        if (!authResult.Succeeded)
        {
            // Missing vs. not-owned/view-only both resolve to 404 (anti-enumeration).
            return NotFound();
        }

        document.Name = request.Name;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    [HttpPost("{id}/shares")]
    public async Task<IActionResult> CreateShare(Guid id, CreateShareRequest request, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.FindAsync(new object[] { id }, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        var authResult = await _authorizationService.AuthorizeAsync(User, document, new OwnerRequirement());
        if (!authResult.Succeeded)
        {
            return NotFound();
        }

        // Owner-only authorization above already guarantees the caller owns
        // this document, so their tenant and the document's tenant should
        // never disagree. Asserting it rather than assuming it: a silent
        // mismatch here would mean a share got labelled with the wrong
        // tenant, which nothing downstream would catch on its own.
        if (document.TenantId != _currentTenant.TenantId)
        {
            throw new InvalidOperationException(
                "Document owner's current tenant does not match the document's own tenant — " +
                "this should be unreachable given owner-only authorization.");
        }

        if (request.Role is not { } role || !Enum.IsDefined(role))
        {
            return BadRequest("Invalid role.");
        }

        var recipient = await _userManager.FindByEmailAsync(request.Email);
        if (recipient is null)
        {
            return BadRequest("User not found.");
        }

        if (recipient.Id == document.OwnerId)
        {
            return BadRequest("Cannot share a document with its owner.");
        }

        var alreadyShared = await _dbContext.DocumentShares
            .AnyAsync(share => share.DocumentId == id && share.UserId == recipient.Id, cancellationToken);
        if (alreadyShared)
        {
            return Conflict("Document is already shared with this user.");
        }

        // The share's tenant follows the document being shared, not the
        // recipient — same-tenant recipient enforcement is later work, but
        // the share itself must always be labelled with its document's
        // tenant, never the recipient's.
        var documentShare = new DocumentShare
        {
            Id = Guid.NewGuid(),
            DocumentId = id,
            UserId = recipient.Id,
            TenantId = document.TenantId,
            UserRole = role
        };

        _dbContext.DocumentShares.Add(documentShare);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The pre-check above covers the common case; this catches the rare
            // race where the same share was inserted concurrently, after the
            // check but before this save. Re-verify rather than assume the
            // unique index is why it failed — some other DbUpdateException (a
            // transient connection issue, an unrelated constraint) would
            // otherwise get misreported as "already shared".
            var stillAlreadyShared = await _dbContext.DocumentShares
                .AnyAsync(share => share.DocumentId == id && share.UserId == recipient.Id, cancellationToken);
            if (stillAlreadyShared)
            {
                return Conflict("Document is already shared with this user.");
            }

            throw;
        }

        return Ok(new { ShareId = documentShare.Id });
    }

    [HttpGet("{id}/shares")]
    public async Task<IActionResult> ListShares(Guid id, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.FindAsync(new object[] { id }, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        var authResult = await _authorizationService.AuthorizeAsync(User, document, new OwnerRequirement());
        if (!authResult.Succeeded)
        {
            return NotFound();
        }

        var shares = await _dbContext.DocumentShares
            .Where(share => share.DocumentId == id)
            .ToListAsync(cancellationToken);

        return Ok(shares);
    }

    [HttpDelete("{id}/shares/{shareId}")]
    public async Task<IActionResult> DeleteShare(Guid id, Guid shareId, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.FindAsync(new object[] { id }, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        var authResult = await _authorizationService.AuthorizeAsync(User, document, new OwnerRequirement());
        if (!authResult.Succeeded)
        {
            return NotFound();
        }

        var share = await _dbContext.DocumentShares
            .SingleOrDefaultAsync(s => s.Id == shareId && s.DocumentId == id, cancellationToken);
        if (share is null)
        {
            return NotFound();
        }

        _dbContext.DocumentShares.Remove(share);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    private async Task<bool> IsFolderOwnedByCurrentUser(Guid folderId, CancellationToken cancellationToken)
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

public record CreateDocumentRequest(string Name, Guid? FolderId, string ContentType, long Size);
public record UpdateDocumentRequest(string Name);
public record CreateShareRequest(string Email, DocumentShare.Role? Role);
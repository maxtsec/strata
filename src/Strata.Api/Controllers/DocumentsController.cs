// Strata.Api/Controllers/DocumentsController.cs
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Strata.Api.Authorization;
using Strata.Application.Persistence;
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

    public DocumentsController(
        IApplicationDbContext dbContext,
        IFileStorage fileStorage,
        IAuthorizationService authorizationService,
        UserManager<ApplicationUser> userManager)
    {
        _dbContext = dbContext;
        _fileStorage = fileStorage;
        _authorizationService = authorizationService;
        _userManager = userManager;
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

        var documentShare = new DocumentShare
        {
            Id = Guid.NewGuid(),
            DocumentId = id,
            UserId = recipient.Id,
            UserRole = request.Role
        };

        _dbContext.DocumentShares.Add(documentShare);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Rare race: two requests shared the same document with the same
            // user concurrently: the pre-check above passed for both, but the
            // unique index only let one insert through.
            return Conflict("Document is already shared with this user.");
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
public record CreateShareRequest(string Email, DocumentShare.Role Role);
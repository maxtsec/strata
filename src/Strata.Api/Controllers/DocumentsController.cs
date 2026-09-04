// Strata.Api/Controllers/DocumentsController.cs
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Strata.Api.Authorization;
using Strata.Application.Persistence;
using Strata.Domain.Documents;

namespace Strata.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DocumentsController : ControllerBase
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IFileStorage _fileStorage;
    private readonly IAuthorizationService _authorizationService;

    public DocumentsController(IApplicationDbContext dbContext, IFileStorage fileStorage, IAuthorizationService authorizationService)
    {
        _dbContext = dbContext;
        _fileStorage = fileStorage;
        _authorizationService = authorizationService;
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

        // TODO: DocumentShare — a Member/Viewer should also pass here, not just the owner
        var authResult = await _authorizationService.AuthorizeAsync(User, document, new OwnerRequirement());
        if (!authResult.Succeeded)
        {
            // Missing vs. not-owned both resolve to 404, so a response can't be used
            // to tell whether an id exists at all (anti-enumeration).
            return NotFound();
        }

        var downloadUri = await _fileStorage.GetDownloadUriAsync(id, cancellationToken);
        return Ok(new { DownloadUrl = downloadUri });
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
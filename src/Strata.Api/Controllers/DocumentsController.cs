// Strata.Api/Controllers/DocumentsController.cs
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    public DocumentsController(IApplicationDbContext dbContext, IFileStorage fileStorage)
    {
        _dbContext = dbContext;
        _fileStorage = fileStorage;
    }

    private Guid CurrentUserId =>
        Guid.Parse(User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value);

    [HttpPost]
    public async Task<IActionResult> Create(CreateDocumentRequest request, CancellationToken cancellationToken)
    {
        var document = new Document
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            OwnerId = CurrentUserId,
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
        // 3. ToDo: DocumentShare
        if (document.OwnerId != CurrentUserId)
        {
            return Forbid();
        }

        var downloadUri = await _fileStorage.GetDownloadUriAsync(id, cancellationToken);
        return Ok(new { DownloadUrl = downloadUri });
    }
}

public record CreateDocumentRequest(string Name, Guid? FolderId, string ContentType, long Size);
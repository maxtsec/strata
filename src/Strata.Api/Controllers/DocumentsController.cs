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
        // 1. 用 _dbContext.Documents 搵返呢份 document(用 FindAsync 或者 LINQ)
        var document = await _dbContext.Documents.FindAsync(new object[] { id }, cancellationToken);
        // 2. 搵唔到就 return NotFound()
        if (document == null)
        {
            return NotFound();
        }
        // 3. 診吓:淨係 OwnerId 係自己嘅先攞得到 download url,
        //    唔係自己嘅點算?(而家淨係處理「係自己」嘅情況,DocumentShare
        //    嗰種分享機制留返聽日先做,唔使而家諗晒)
        if (document.OwnerId != CurrentUserId)
        {
            return Forbid();
        }
        // 4. 攞返 _fileStorage.GetDownloadUriAsync(...),回應
        var downloadUri = await _fileStorage.GetDownloadUriAsync(id, cancellationToken);
        return Ok(new { DownloadUrl = downloadUri });
    }
}

public record CreateDocumentRequest(string Name, Guid? FolderId, string ContentType, long Size);
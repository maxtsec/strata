namespace Strata.Domain.Documents;

public class Document : IOwnable, ITenantOwned
{
    public Guid Id { get; init; }
    public Guid OwnerId { get; init; }
    public Guid TenantId { get; init; }
    public Guid? FolderId { get; set; }
    public string Name {get; set; } = string.Empty;
    public long Size { get; set; }
    public string ContentType { get; set; } = string.Empty;
}
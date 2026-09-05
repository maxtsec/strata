namespace Strata.Domain.Documents;

public class Folder : IOwnable, ITenantOwned
{
    public Guid Id { get; init; }
    public Guid OwnerId { get; init; }
    public Guid TenantId { get; init; }
    public Guid? ParentFolderId { get; set; }
    public string Name {get; set; } = string.Empty;
}
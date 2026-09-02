namespace Strata.Domain.Documents;

public class Folder
{
    public Guid Id { get; init; }
    public Guid OwnerId { get; init; }
    public Guid? ParentFolderId { get; set; }
    public string Name {get; set; } = string.Empty;
}
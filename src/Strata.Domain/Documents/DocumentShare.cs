namespace Strata.Domain.Documents;

public class DocumentShare : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid DocumentId { get; init; }
    public Guid UserId { get; init; }
    public Guid TenantId { get; init; }
    public enum Role
    {
        Member,
        Viewer
    }
    public Role UserRole { get; set; }
}
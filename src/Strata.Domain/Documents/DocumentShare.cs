namespace Strata.Domain.Documents;

public class DocumentShare
{
    public Guid Id { get; init; }
    public Guid DocumentId { get; init; }
    public Guid UserId { get; init; }
    public enum Role
    {
        Member,
        Viewer
    }
    public Role UserRole { get; set; }
}
namespace Strata.Domain;

public interface IOwnable
{
    public Guid OwnerId { get; }
}
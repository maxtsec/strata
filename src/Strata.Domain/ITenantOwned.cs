namespace Strata.Domain;

public interface ITenantOwned
{
    public Guid TenantId { get; }
}

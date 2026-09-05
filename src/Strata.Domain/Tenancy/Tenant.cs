namespace Strata.Domain.Tenancy;

public class Tenant
{
    public Guid Id { get; init; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}

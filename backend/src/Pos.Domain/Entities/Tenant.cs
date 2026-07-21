namespace Pos.Domain.Entities;

public class Tenant
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public TenantType Type { get; set; }
}

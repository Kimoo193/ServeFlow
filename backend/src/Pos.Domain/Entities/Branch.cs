namespace Pos.Domain.Entities;

public class Branch
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }
}

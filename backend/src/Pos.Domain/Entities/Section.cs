namespace Pos.Domain.Entities;

public class Section
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public required string Name { get; set; }
}

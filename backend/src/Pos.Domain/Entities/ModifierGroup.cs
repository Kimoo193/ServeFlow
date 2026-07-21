namespace Pos.Domain.Entities;

public class ModifierGroup
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public int MinSelect { get; set; }
    public int MaxSelect { get; set; }
    public bool Required { get; set; }
}

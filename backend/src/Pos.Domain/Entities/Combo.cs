namespace Pos.Domain.Entities;

public class Combo
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }
}

namespace Pos.Domain.Entities;

public class OrderLineModifier
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OrderLineId { get; set; }
    public required string Name { get; set; }
    public decimal PriceDelta { get; set; }
}

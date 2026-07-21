namespace Pos.Domain.Entities;

public class Modifier
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid GroupId { get; set; }
    public required string Name { get; set; }
    public decimal PriceDelta { get; set; }
}

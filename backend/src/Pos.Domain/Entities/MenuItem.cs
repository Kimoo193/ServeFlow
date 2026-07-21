namespace Pos.Domain.Entities;

public class MenuItem
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CategoryId { get; set; }
    public Guid StationId { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }
    public Availability Availability { get; set; }
}

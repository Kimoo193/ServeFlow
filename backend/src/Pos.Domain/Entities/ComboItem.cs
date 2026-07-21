namespace Pos.Domain.Entities;

public class ComboItem
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ComboId { get; set; }
    public Guid ItemId { get; set; }
    public int Quantity { get; set; }
}

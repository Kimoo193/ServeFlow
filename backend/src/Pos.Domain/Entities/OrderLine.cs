namespace Pos.Domain.Entities;

public class OrderLine
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OrderId { get; set; }
    public Guid ItemId { get; set; }
    public Guid StationId { get; set; }
    public Guid? FireBatchId { get; set; }
    public int Sequence { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public OrderLineStatus Status { get; set; }
}

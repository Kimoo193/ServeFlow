namespace Pos.Domain.Entities;

public class Order
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? TableId { get; set; }
    public Guid WaiterId { get; set; }
    public OrderType OrderType { get; set; }
    public OrderStatus Status { get; set; }
    public int? DailyNo { get; set; }
    public string? CustomerName { get; set; }
    public int RowVersion { get; set; }
}

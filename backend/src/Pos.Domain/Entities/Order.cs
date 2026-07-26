using System.Linq.Expressions;

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
    public decimal? SubtotalAmount { get; set; }
    public decimal? ServiceAmount { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? DiscountAmount { get; set; }
    public decimal? TotalAmount { get; set; }
    public string? Currency { get; set; }

    public int RowVersion { get; set; }

    /// <summary>
    /// Whether the order can still be changed — lines added, edited, voided or fired.
    /// Once it leaves Open/InProgress it has been closed or cancelled and is final.
    /// </summary>
    public bool IsAcceptingChanges => Status is OrderStatus.Open or OrderStatus.InProgress;

    /// <summary>
    /// The queryable form of <see cref="IsAcceptingChanges"/>.
    /// </summary>
    /// <remarks>
    /// EF Core cannot translate the computed property into SQL, so without this the status
    /// list would have to be restated inside every <c>Where</c> — which is how it came to be
    /// written out at four separate call sites, free to drift apart.
    /// </remarks>
    public static Expression<Func<Order, bool>> AcceptingChanges =>
        o => o.Status == OrderStatus.Open || o.Status == OrderStatus.InProgress;
}

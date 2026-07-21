namespace Pos.Domain.Entities;

public class DiningTable
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SectionId { get; set; }
    public required string Label { get; set; }
    public TableStatus Status { get; set; } = TableStatus.Available;
}

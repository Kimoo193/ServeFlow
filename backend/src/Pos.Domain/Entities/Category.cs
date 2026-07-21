namespace Pos.Domain.Entities;

public class Category
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }
}

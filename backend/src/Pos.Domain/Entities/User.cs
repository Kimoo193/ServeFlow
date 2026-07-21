namespace Pos.Domain.Entities;

public class User
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string DisplayName { get; set; }

    public Role Role { get; set; }
}

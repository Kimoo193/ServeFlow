using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Pos.Infrastructure.Auth;
using Pos.Infrastructure.Persistence;

namespace Pos.Api.Hubs;

public sealed class PosHub(PosDbContext dbContext) : Hub
{
    public async Task JoinStationGroup(Guid stationId)
    {
        var tenantId = ReadTenantId();
        if (tenantId == Guid.Empty)
            throw new HubException("Required tenant claim is missing.");

        await using var transaction = await dbContext.Database.BeginTransactionAsync(Context.ConnectionAborted);
        await dbContext.Database.SetTenantAsync(tenantId, Context.ConnectionAborted);

        var stationExists = await dbContext.Stations.AnyAsync(s => s.Id == stationId, Context.ConnectionAborted);
        if (!stationExists)
            throw new HubException("Station not found or does not belong to this tenant.");

        await transaction.CommitAsync(Context.ConnectionAborted);

        await Groups.AddToGroupAsync(Context.ConnectionId, $"station:{stationId}", Context.ConnectionAborted);
    }

    public async Task JoinBranchGroup(Guid branchId)
    {
        var tenantId = ReadTenantId();
        if (tenantId == Guid.Empty)
            throw new HubException("Required tenant claim is missing.");

        await using var transaction = await dbContext.Database.BeginTransactionAsync(Context.ConnectionAborted);
        await dbContext.Database.SetTenantAsync(tenantId, Context.ConnectionAborted);

        var branchExists = await dbContext.Branches.AnyAsync(b => b.Id == branchId, Context.ConnectionAborted);
        if (!branchExists)
            throw new HubException("Branch not found or does not belong to this tenant.");

        await transaction.CommitAsync(Context.ConnectionAborted);

        await Groups.AddToGroupAsync(Context.ConnectionId, $"branch:{branchId}", Context.ConnectionAborted);
    }

    public async Task JoinWaiterGroup()
    {
        var userId = ReadUserId();
        if (userId == Guid.Empty)
            throw new HubException("Required user_id claim is missing.");

        await Groups.AddToGroupAsync(Context.ConnectionId, $"waiter:{userId}", Context.ConnectionAborted);
    }

    private Guid ReadTenantId()
    {
        var value = Context.User?.FindFirst(JwtTenantContext.TenantIdClaim)?.Value;
        return value is not null && Guid.TryParse(value, out var id) ? id : Guid.Empty;
    }

    private Guid ReadUserId()
    {
        var value = Context.User?.FindFirst(JwtTenantContext.UserIdClaim)?.Value;
        return value is not null && Guid.TryParse(value, out var id) ? id : Guid.Empty;
    }
}

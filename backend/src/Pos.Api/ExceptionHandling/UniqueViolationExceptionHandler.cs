using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Pos.Api.ExceptionHandling;

public sealed class UniqueViolationExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pgEx })
        {
            return false;
        }

        var detail = pgEx.ConstraintName switch
        {
            "IX_orders_table_id" => "table already has an open order",
            _ => "A unique constraint was violated.",
        };

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Constraint violation",
                Detail = detail,
            },
            cancellationToken);

        return true;
    }
}

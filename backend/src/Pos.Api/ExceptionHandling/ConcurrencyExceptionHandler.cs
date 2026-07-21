using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Pos.Api.ExceptionHandling;

public sealed class ConcurrencyExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not DbUpdateConcurrencyException)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Concurrency conflict",
                Detail = "Order has been modified.",
            },
            cancellationToken);

        return true;
    }
}

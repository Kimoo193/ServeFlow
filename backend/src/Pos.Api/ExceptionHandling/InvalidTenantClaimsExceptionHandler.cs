using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Pos.Application.Common;

namespace Pos.Api.ExceptionHandling;

/// <summary>
/// Maps <see cref="InvalidTenantClaimsException"/> to a clean 401 ProblemDetails response
/// instead of an unhandled 500 with a stack trace. Handles only this one exception type —
/// any other exception is left unhandled and returned to be handled elsewhere, since those
/// represent real server faults, not a bad token.
/// </summary>
public sealed class InvalidTenantClaimsExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not InvalidTenantClaimsException invalidClaims)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Invalid authentication claims",
                Detail = invalidClaims.Message,
            },
            cancellationToken);

        return true;
    }
}

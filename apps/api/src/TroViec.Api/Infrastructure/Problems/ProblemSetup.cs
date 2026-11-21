using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using TroViec.Api.Infrastructure.Db;

namespace TroViec.Api.Infrastructure.Problems;

public static class ProblemSetup
{
    /// <summary>RFC 9457 problem+json for every error response, with "code" and "traceId" (docs/04-api.md, Lỗi).</summary>
    public static IServiceCollection AddProblems(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var problem = context.ProblemDetails;
            // Framework-generated problems (404 route, 401, 405…) get a code too.
            if (!problem.Extensions.ContainsKey("code"))
            {
                var code = DefaultCodeFor(problem.Status ?? context.HttpContext.Response.StatusCode);
                problem.Extensions["code"] = code;
                problem.Type = ErrorCatalog.TypeBaseUri + code;
                problem.Title = ErrorCatalog.Get(code).Title;
            }
            problem.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
            problem.Extensions.TryAdd("errors", null);
        });
        services.AddExceptionHandler<ProblemExceptionHandler>();
        return services;
    }

    public static ProblemDetails ToProblemDetails(ProblemException ex)
    {
        var info = ErrorCatalog.Get(ex.Code);
        var problem = new ProblemDetails
        {
            Type = ErrorCatalog.TypeBaseUri + ex.Code,
            Title = info.Title,
            Status = info.Status,
            Detail = ex.Detail,
        };
        problem.Extensions["code"] = ErrorCatalog.IsKnown(ex.Code) ? ex.Code : "system.error";
        problem.Extensions["errors"] = ex.Errors;
        foreach (var (key, value) in ex.Extensions ?? new Dictionary<string, object?>())
            problem.Extensions[key] = value;
        return problem;
    }

    private static string DefaultCodeFor(int status) => status switch
    {
        400 => "validation.failed",
        401 => "auth.unauthenticated",
        403 => "auth.forbidden",
        404 => "resource.not_found",
        429 => "rate_limited",
        503 => "system.retry_later",
        _ => "system.error",
    };
}

/// <summary>Turns ProblemException and mappable PostgresException into problem+json; anything else is a 500.</summary>
internal sealed class ProblemExceptionHandler(
    IProblemDetailsService problemDetails,
    IHostEnvironment environment,
    ILogger<ProblemExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var problemException = exception switch
        {
            ProblemException pe => pe,
            PostgresException pg => PgErrorMapper.Map(pg, includeConstraintName: environment.IsDevelopment()),
            _ => null,
        };

        ProblemDetails problem;
        if (problemException is null)
        {
            // Only the exception type is logged; messages may contain user data.
            logger.LogError("Unhandled {ExceptionType}", exception.GetType().FullName);
            problem = ProblemSetup.ToProblemDetails(new ProblemException("system.error"));
        }
        else
        {
            problem = ProblemSetup.ToProblemDetails(problemException);
        }

        httpContext.Response.StatusCode = problem.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}

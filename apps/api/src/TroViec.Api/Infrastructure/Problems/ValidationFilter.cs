using FluentValidation;

namespace TroViec.Api.Infrastructure.Problems;

/// <summary>
/// Runs the FluentValidation validator for the endpoint's <typeparamref name="TRequest"/> argument and turns
/// failures into 400 validation.failed with errors keyed by camelCase field name (docs/04-api.md, Lỗi).
/// </summary>
public sealed class ValidationFilter<TRequest>(IValidator<TRequest> validator) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();
        if (request is null) throw new ProblemException("validation.failed");

        var result = await validator.ValidateAsync(request, context.HttpContext.RequestAborted);
        if (!result.IsValid)
        {
            var errors = result.Errors
                .GroupBy(e => CamelCase(e.PropertyName))
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
            throw new ProblemException("validation.failed", errors: errors);
        }
        return await next(context);
    }

    private static string CamelCase(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
}

public static class ValidationFilterExtensions
{
    public static RouteHandlerBuilder Validate<TRequest>(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<ValidationFilter<TRequest>>();
}

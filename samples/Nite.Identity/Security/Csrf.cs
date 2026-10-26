using Microsoft.AspNetCore.Antiforgery;

namespace Nite.Identity.Security;

/// <summary>
/// Cross-site request forgery protection for JSON endpoints. Every page carries a form token (see
/// ClientConfig in Program.cs) that the app sends back in a header on anything that changes data;
/// another site can make the browser send the cookies, but cannot read the token. ASP.NET Core
/// checks form posts on its own; JSON endpoints need this filter.
/// </summary>
public static class Csrf
{
    public const string HeaderName = "X-CSRF-Token";

    public static async ValueTask<object?> Validate(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var method = http.Request.Method;
        var safe = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);
        if (!safe && !await http.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(http))
        {
            return Results.Problem("The page's form token is missing or out of date. Reload the page and try again.", statusCode: StatusCodes.Status400BadRequest);
        }
        return await next(context);
    }
}

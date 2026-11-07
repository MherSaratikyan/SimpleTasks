using System.Security.Cryptography;
using System.Text;

namespace SimpleTasks.Identity.Security;

/// <summary>Protects the calls the web app makes before a user has a token.</summary>
public sealed class InternalKeyFilter(IConfiguration configuration) : IEndpointFilter
{
    public const string Header = "X-Internal-Key";

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = configuration["Internal:ApiKey"];
        var supplied = context.HttpContext.Request.Headers[Header].ToString();
        if (string.IsNullOrEmpty(expected)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied)))
        {
            return ValueTask.FromResult<object?>(Results.Unauthorized());
        }
        return next(context);
    }
}

using MfaSrv.Server.Services;

namespace MfaSrv.Server.Middleware;

public class SetupRedirectMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SetupService _setupService;

    private static readonly string[] AllowedPrefixes = { "/setup", "/api/setup", "/health", "/ready", "/status" };

    public SetupRedirectMiddleware(RequestDelegate next, SetupService setupService)
    {
        _next = next;
        _setupService = setupService;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_setupService.IsSetupRequired())
        {
            var path = context.Request.Path.Value ?? string.Empty;

            var allowed = false;
            foreach (var prefix in AllowedPrefixes)
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    allowed = true;
                    break;
                }
            }

            if (!allowed)
            {
                context.Response.Redirect("/setup");
                return;
            }
        }

        await _next(context);
    }
}

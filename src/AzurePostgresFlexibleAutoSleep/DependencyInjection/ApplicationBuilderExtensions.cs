using Microsoft.AspNetCore.Builder;

namespace AzurePostgresFlexibleAutoSleep.DependencyInjection;

public static class ApplicationBuilderExtensions
{
    public static IApplicationBuilder UseAzurePostgresAutoSleep(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<AutoWakeMiddleware>();
    }
}

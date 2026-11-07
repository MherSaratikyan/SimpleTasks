using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace SimpleTasks.ServiceDefaults;

public static class Jwt
{
    public const string Issuer = "simpletasks-identity";
    public const string Audience = "simpletasks-services";
    public const string SiteAdminClaim = "site_admin";
}

public static class ServiceDefaultsExtensions
{
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });
        var telemetry = builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation());
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.UseOtlpExporter();
        }

        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });
        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = check => check.Tags.Contains("live") });
        return app;
    }

    /// <summary>
    /// Connects to this service's own database. Server, login and password come
    /// unchanged from DefaultConnection; only the database name differs.
    /// </summary>
    public static string ServiceConnectionString(this IConfiguration configuration, string database)
    {
        var shared = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("The DefaultConnection connection string is missing.");
        return new SqlConnectionStringBuilder(shared) { InitialCatalog = database }.ConnectionString;
    }

    public static IServiceCollection AddServiceDatabase<TDb>(this WebApplicationBuilder builder, string database)
        where TDb : DbContext
    {
        var connectionString = builder.Configuration.ServiceConnectionString(database);
        return builder.Services.AddDbContext<TDb>(options =>
            options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
    }

    /// <summary>Applies this service's migrations, creating its database if needed.</summary>
    public static async Task MigrateDatabaseAsync<TDb>(this WebApplication app) where TDb : DbContext
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDb>();
        await db.Database.MigrateAsync();
    }

    public static void AddServiceAuthentication(this WebApplicationBuilder builder)
    {
        var identityUrl = builder.Configuration["services:identity:http:0"] ?? "http://identity:8080";
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = false;
                options.MetadataAddress = $"{identityUrl.TrimEnd('/')}/.well-known/openid-configuration";
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = Jwt.Issuer,
                    ValidAudience = Jwt.Audience,
                    NameClaimType = "name",
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });
        builder.Services.AddAuthorization();
    }

    public static Guid UserId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("sub")!);

    public static bool IsSiteAdmin(this ClaimsPrincipal user) => user.HasClaim(Jwt.SiteAdminClaim, "true");

    public static string DisplayName(this ClaimsPrincipal user) => user.FindFirstValue("name") ?? "Someone";
}

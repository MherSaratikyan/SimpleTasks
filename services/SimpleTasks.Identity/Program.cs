using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using SimpleTasks.Contracts;
using SimpleTasks.Identity.Data;
using SimpleTasks.Identity.Features;
using SimpleTasks.Identity.Security;
using SimpleTasks.Messaging;
using SimpleTasks.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddServiceDatabase<IdentityDb>(IdentityDb.DatabaseName);
builder.AddServiceAuthentication();

var tokens = new TokenIssuer();
builder.Services.AddSingleton(tokens);

// Identity validates its own tokens with the key it holds rather than calling itself.
builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
{
    options.Configuration = new OpenIdConnectConfiguration();
    options.TokenValidationParameters.IssuerSigningKey = tokens.Key;
});

builder.Services.AddMessaging<IdentityDb>("identity", messaging =>
    messaging.Handle<ProjectInviteRequested, ProjectInviteRequestedHandler>());

var app = builder.Build();

await app.MigrateDatabaseAsync<IdentityDb>();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    await DevUsers.SeedAsync(
        scope.ServiceProvider.GetRequiredService<IdentityDb>(),
        app.Configuration["Identity:FirstAdminEmail"]);

    app.MapGet("/internal/dev-users", async (IdentityDb db, CancellationToken ct) =>
        await db.Users.AsNoTracking().OrderBy(user => user.Email)
            .Select(user => new { user.Email, user.DisplayName, user.IsSiteAdmin })
            .ToListAsync(ct))
        .AddEndpointFilter<InternalKeyFilter>();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapGet("/.well-known/openid-configuration", (HttpContext http) =>
{
    var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
    return Results.Json(new
    {
        issuer = Jwt.Issuer,
        jwks_uri = $"{baseUrl}/.well-known/jwks.json",
        id_token_signing_alg_values_supported = new[] { "RS256" }
    });
});
app.MapGet("/.well-known/jwks.json", (TokenIssuer issuer) => Results.Json(issuer.Jwks()));
app.MapSignIn();
app.MapInvites();
app.MapUsers();

await app.RunAsync();

public sealed class IdentityEntryPoint;

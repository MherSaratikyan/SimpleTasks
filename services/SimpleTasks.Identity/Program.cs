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
builder.Services.AddSingleton<Microsoft.AspNetCore.Identity.IPasswordHasher<User>, Microsoft.AspNetCore.Identity.PasswordHasher<User>>();

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

await using (var scope = app.Services.CreateAsyncScope())
{
    await FirstAdmin.EnsureAsync(
        scope.ServiceProvider.GetRequiredService<IdentityDb>(),
        app.Configuration["Identity:FirstAdminEmail"],
        app.Configuration["App:BaseUrl"] ?? "http://localhost:5080",
        app.Logger);
}

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    await DevUsers.SeedAsync(scope.ServiceProvider.GetRequiredService<IdentityDb>());

    app.MapGet("/internal/dev-users", async (IdentityDb db, CancellationToken ct) =>
        await db.Users.AsNoTracking().OrderBy(user => user.Email)
            .Select(user => new { user.Email, user.DisplayName, user.IsSiteAdmin })
            .ToListAsync(ct))
        .AddEndpointFilter<InternalKeyFilter>();

    // Development only: signs in a seeded account without a password.
    app.MapPost("/internal/dev-sign-in", async (DevSignInRequest request, IdentityDb db, CancellationToken ct) =>
    {
        var email = Login.Normalize(request.Email);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Email == email && user.IsActive, ct);
        return user is null ? Results.Forbid() : Results.Ok(user.ToResponse());
    }).AddEndpointFilter<InternalKeyFilter>();
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
app.MapLogin();
app.MapInvites();
app.MapUsers();

await app.RunAsync();

public sealed class IdentityEntryPoint;

public sealed record DevSignInRequest(string Email);

using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using SimpleTasks.Services;
using SimpleTasks.ServiceDefaults;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToFolder("/Account");
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AuthorizeFolder("/Admin", "SiteAdmin");
});

const string ExternalScheme = "External";
var authentication = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Denied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.Name = "SimpleTasks.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
    })
    .AddCookie(ExternalScheme);

var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
var googleConfigured = !string.IsNullOrEmpty(googleClientId) && !string.IsNullOrEmpty(googleClientSecret);
if (googleConfigured)
{
    authentication.AddGoogle(options =>
    {
        options.SignInScheme = ExternalScheme;
        options.ClientId = googleClientId!;
        options.ClientSecret = googleClientSecret!;
        options.Events.OnCreatingTicket = context =>
        {
            // An unverified address proves nothing about who owns it.
            var verified = context.User.TryGetProperty("email_verified", out var value) && value.ValueKind == JsonValueKind.True;
            if (!verified)
            {
                context.Fail("Google hasn't verified this email address.");
                return Task.CompletedTask;
            }
            if (context.User.TryGetProperty("picture", out var picture) && picture.GetString() is { } url)
            {
                context.Identity!.AddClaim(new Claim(AppClaims.Avatar, url));
            }
            return Task.CompletedTask;
        };
    });
}
builder.Services.Configure<GoogleSignInOptions>(options => options.Enabled = googleConfigured);

builder.Services.AddAuthorization(options =>
    options.AddPolicy("SiteAdmin", policy => policy.RequireClaim(AppClaims.SiteAdmin, "true")));

builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<UserTime>();
builder.Services.AddSingleton<ServiceTokens>();
builder.Services.AddTransient<ServiceTokenHandler>();

builder.Services.AddHttpClient<IdentityInternalClient>(client =>
{
    client.BaseAddress = new Uri("http://identity");
    client.DefaultRequestHeaders.Add("X-Internal-Key", builder.Configuration["Internal:ApiKey"]);
});
builder.Services.AddHttpClient<IdentityClient>(client => client.BaseAddress = new Uri("http://identity"))
    .AddHttpMessageHandler<ServiceTokenHandler>();
builder.Services.AddHttpClient<WorkClient>(client => client.BaseAddress = new Uri("http://work"))
    .AddHttpMessageHandler<ServiceTokenHandler>();
builder.Services.AddHttpClient<NotificationsClient>(client => client.BaseAddress = new Uri("http://notifications"))
    .AddHttpMessageHandler<ServiceTokenHandler>();

// The bell's live connection passes through this app, which swaps the cookie for a service token.
var notificationsUrl = builder.Configuration["services:notifications:http:0"] ?? "http://notifications:8080";
builder.Services.AddReverseProxy()
    .LoadFromMemory(
        [new RouteConfig
        {
            RouteId = "notification-hub",
            ClusterId = "notifications",
            AuthorizationPolicy = "Default",
            Match = new RouteMatch { Path = "/hubs/notifications/{**rest}" }
        }],
        [new ClusterConfig
        {
            ClusterId = "notifications",
            Destinations = new Dictionary<string, DestinationConfig>
            {
                ["service"] = new() { Address = notificationsUrl }
            }
        }])
    .AddTransforms(transforms => transforms.AddRequestTransform(async context =>
    {
        context.ProxyRequest.Headers.Remove("Cookie");
        var id = context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var tokens = context.HttpContext.RequestServices.GetRequiredService<ServiceTokens>();
        context.ProxyRequest.Headers.Authorization = new("Bearer", await tokens.GetAsync(Guid.Parse(id), context.HttpContext.RequestAborted));
    }));

var app = builder.Build();

app.UseExceptionHandler("/Error");
app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();

// A deactivated user loses access at their next request, not when their cookie expires.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (AccountRevokedException)
    {
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!context.Response.HasStarted)
        {
            context.Response.Redirect("/Account/Denied");
        }
    }
    catch (ApiException exception) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = (int)exception.Status;
    }
});

app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapRazorPages();
app.MapReverseProxy();

await app.RunAsync();

public sealed class WebEntryPoint;

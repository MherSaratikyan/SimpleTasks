using SimpleTasks.Contracts;
using SimpleTasks.Messaging;
using SimpleTasks.ServiceDefaults;
using SimpleTasks.Work.Data;
using SimpleTasks.Work.Features;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddServiceDatabase<WorkDb>(WorkDb.DatabaseName);
builder.AddServiceAuthentication();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ProjectGate>();
builder.Services.AddScoped<LegacyTaskImport>();
builder.Services.AddMessaging<WorkDb>("work", messaging => messaging
    .Handle<UserChanged, UserChangedHandler>()
    .Handle<ProjectMembershipGranted, ProjectMembershipGrantedHandler>());

var app = builder.Build();

await app.MigrateDatabaseAsync<WorkDb>();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<LegacyTaskImport>().RunAsync();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapProjects();
app.MapTickets();
app.MapStatistics();

await app.RunAsync();

public sealed class WorkEntryPoint;

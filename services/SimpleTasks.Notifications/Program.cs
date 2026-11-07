using Microsoft.AspNetCore.SignalR;
using SimpleTasks.Contracts;
using SimpleTasks.Messaging;
using SimpleTasks.Notifications.Data;
using SimpleTasks.Notifications.Features;
using SimpleTasks.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddServiceDatabase<NotificationsDb>(NotificationsDb.DatabaseName);
builder.AddServiceAuthentication();

builder.Services.AddSignalR();
builder.Services.AddSingleton<IUserIdProvider, SubjectUserIdProvider>();

builder.Services.AddScoped<NotificationWriter>();
builder.Services.AddScoped<IAfterCommit>(provider => provider.GetRequiredService<NotificationWriter>());
builder.Services.AddScoped<ReminderRunner>();
builder.Services.AddHostedService<ReminderWorker>();
builder.Services.AddHostedService<EmailWorker>();

if (builder.Environment.IsDevelopment() && string.IsNullOrEmpty(builder.Configuration["Gmail:AppPassword"]))
{
    builder.Services.AddSingleton<IEmailSender, LogEmailSender>();
}
else
{
    builder.Services.AddSingleton<IEmailSender, GmailSender>();
}

builder.Services.AddMessaging<NotificationsDb>("notifications", messaging => messaging
    .Handle<UserChanged, UserChangedHandler>()
    .Handle<InviteCreated, InviteCreatedHandler>()
    .Handle<TicketChanged, TicketChangedHandler>()
    .Handle<TicketAssigned, TicketAssignedHandler>()
    .Handle<CommentAdded, CommentAddedHandler>()
    .Handle<EstimateChanged, EstimateChangedHandler>()
    .Handle<EstimateThresholdCrossed, EstimateThresholdCrossedHandler>());

var app = builder.Build();

await app.MigrateDatabaseAsync<NotificationsDb>();

app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapNotifications();

await app.RunAsync();

public sealed class NotificationsEntryPoint;

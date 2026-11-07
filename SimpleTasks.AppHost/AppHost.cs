using System.Security.Cryptography;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// The services share your existing connection string, read once from its current
// file. It stays there unedited and reaches them as a masked parameter.
var settingsFile = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "SimpleTasks", "appsettings.json"));
var sharedSettings = new ConfigurationBuilder().AddJsonFile(settingsFile, optional: true).Build();

builder.Configuration["Parameters:internal-api-key"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var internalKey = builder.AddParameter("internal-api-key", secret: true);

var messaging = builder.AddRabbitMQ("messaging").WithManagementPlugin().WithDataVolume("simpletasks-rabbitmq");

ReferenceExpression sqlConnection;
if (builder.Configuration.GetValue<bool>("UseContainerSql"))
{
    // Used by the end-to-end test so it never touches your real server.
    sqlConnection = builder.AddSqlServer("sql").Resource.ConnectionStringExpression;
}
else
{
    builder.Configuration["Parameters:sql-connection"] = sharedSettings.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException($"No DefaultConnection in {settingsFile}.");
    var parameter = builder.AddParameter("sql-connection", secret: true);
    sqlConnection = ReferenceExpression.Create($"{parameter}");
}

var identity = builder.AddProject<Projects.SimpleTasks_Identity>("identity")
    .WithEnvironment("ConnectionStrings__DefaultConnection", sqlConnection)
    .WithEnvironment("Internal__ApiKey", internalKey)
    .WithEnvironment("Identity__FirstAdminEmail", builder.Configuration["FirstAdminEmail"] ?? string.Empty)
    .WithReference(messaging).WaitFor(messaging);

var work = builder.AddProject<Projects.SimpleTasks_Work>("work")
    .WithEnvironment("ConnectionStrings__DefaultConnection", sqlConnection)
    .WithReference(messaging).WaitFor(messaging)
    .WithReference(identity).WaitFor(identity);

var notifications = builder.AddProject<Projects.SimpleTasks_Notifications>("notifications")
    .WithEnvironment("ConnectionStrings__DefaultConnection", sqlConnection)
    .WithReference(messaging).WaitFor(messaging)
    .WithReference(identity).WaitFor(identity);

builder.AddProject<Projects.SimpleTasks>("web")
    .WithEnvironment("Internal__ApiKey", internalKey)
    .WithExternalHttpEndpoints()
    .WithReference(identity).WaitFor(identity)
    .WithReference(work).WaitFor(work)
    .WithReference(notifications).WaitFor(notifications);

builder.Build().Run();

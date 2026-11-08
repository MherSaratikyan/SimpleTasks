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

// PublicUrl is the address people open, such as http://192.168.1.6:5080 for your Wi-Fi. Email links use it.
// Left unset, the app is reachable from this computer only.
var publicUrl = (builder.Configuration["PublicUrl"] ?? "http://localhost:5080").TrimEnd('/');
var onNetwork = !new Uri(publicUrl).IsLoopback;

// UseContainerSql gives a throwaway run: its own SQL Server and broker, nothing kept.
var throwaway = builder.Configuration.GetValue<bool>("UseContainerSql");

var messaging = builder.AddRabbitMQ("messaging").WithManagementPlugin();
if (!throwaway)
{
    messaging.WithDataVolume("simpletasks-rabbitmq");
}

IResourceBuilder<SqlServerServerResource>? containerSql = null;
ReferenceExpression sqlConnection;
if (throwaway)
{
    containerSql = builder.AddSqlServer("sql");
    sqlConnection = containerSql.Resource.ConnectionStringExpression;
}
else
{
    builder.Configuration["Parameters:sql-connection"] = sharedSettings.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException($"No DefaultConnection in {settingsFile}.");
    var parameter = builder.AddParameter("sql-connection", secret: true);
    sqlConnection = ReferenceExpression.Create($"{parameter}");
}

IResourceBuilder<ProjectResource> WithSql(IResourceBuilder<ProjectResource> project)
{
    project.WithEnvironment("ConnectionStrings__DefaultConnection", sqlConnection);
    project.WithHttpHealthCheck("/health");
    return containerSql is null ? project : project.WaitFor(containerSql);
}

var identity = WithSql(builder.AddProject<Projects.SimpleTasks_Identity>("identity"))
    .WithEnvironment("App__BaseUrl", publicUrl)
    .WithEnvironment("Internal__ApiKey", internalKey)
    .WithEnvironment("Identity__FirstAdminEmail", builder.Configuration["FirstAdminEmail"] ?? string.Empty)
    .WithReference(messaging).WaitFor(messaging);

var work = WithSql(builder.AddProject<Projects.SimpleTasks_Work>("work"))
    .WithReference(messaging).WaitFor(messaging)
    .WithReference(identity).WaitFor(identity);

var notifications = WithSql(builder.AddProject<Projects.SimpleTasks_Notifications>("notifications"))
    .WithEnvironment("App__BaseUrl", publicUrl)
    .WithEnvironment("Reminders__IntervalSeconds", throwaway ? "5" : "60")
    .WithReference(messaging).WaitFor(messaging)
    .WithReference(identity).WaitFor(identity);

builder.AddProject<Projects.SimpleTasks>("web")
    // Only the web app listens beyond this computer; the services and dashboard stay local.
    .WithEndpoint("http", endpoint =>
    {
        if (onNetwork)
        {
            // No Aspire proxy here, so the app sees each visitor's real address.
            endpoint.IsProxied = false;
            endpoint.TargetHost = "0.0.0.0";
        }
    })
    .WithHttpHealthCheck("/health")
    .WithEnvironment("Internal__ApiKey", internalKey)
    .WithExternalHttpEndpoints()
    .WithReference(identity).WaitFor(identity)
    .WithReference(work).WaitFor(work)
    .WithReference(notifications).WaitFor(notifications);

builder.Build().Run();

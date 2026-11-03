# Simple Tasks

A small C# web project for Visual Studio, using ASP.NET Core Razor Pages,
Entity Framework Core, and Microsoft SQL Server. One page and one table.

You can add tasks, mark them complete or incomplete, and delete them. Tasks
remain in SQL Server when you close and restart the application.

## 1. Install the prerequisites

The project targets **.NET 10** and **Visual Studio 2026**.

In Visual Studio Installer, install the **ASP.NET and web development**
workload. Also ensure **SQL Server Express LocalDB** is installed under
Individual components. LocalDB is a Windows development version of SQL
Server Express. A full SQL Server installation also works; see step 3.

If you have Visual Studio 2022, see the compatibility note below before opening
the project. Use the full Visual Studio IDE, not Visual Studio Code, for these steps.

## 2. Open the project

1. Extract the entire ZIP to a normal folder.
2. Open `SimpleTasks.sln` in Visual Studio.
3. Let NuGet restore the single package reference.
4. Set `SimpleTasks` as the startup project if necessary.

## 3. Check your database connection

The default connection in `SimpleTasks/appsettings.json` is:

```json
"DefaultConnection": "Server=(localdb)\\MSSQLLocalDB;Database=SimpleTasksDb;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True"
```

Keep it as written if you installed LocalDB. It uses your Windows account;
you do not need to enter an SQL username or password.

If you already have SQL Server, replace only the server value with the exact
server name you use in SQL Server Management Studio. For example, SQL Server
Express with Windows authentication can use:

```json
"DefaultConnection": "Server=.\\SQLEXPRESS;Database=SimpleTasksDb;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True"
```

The double backslash is JSON escaping. In SSMS or Visual Studio's connection
dialog, enter `(localdb)\MSSQLLocalDB` or `.\SQLEXPRESS` with a single backslash.
Your Windows account must be allowed to create a database on that server.
Use a new, empty database name for this example.

## 4. Run it

Select the **SimpleTasks** launch profile and press **Ctrl+F5**.

The first startup creates `SimpleTasksDb` and the `Tasks` table automatically.
There are no migrations or Package Manager Console commands to run.
Your browser opens at http://localhost:5080.

Try this short check:

1. Add `Learn SQL connections`.
2. Click **Done**, then **Undo**.
3. Close the application and run it again; the task should still exist.
4. Click **Delete**.
5. Try submitting a title containing only spaces; it should be rejected.

## 5. View the stored rows

In Visual Studio, open **View > SQL Server Object Explorer**. Connect to
`(localdb)\MSSQLLocalDB`, expand **Databases > SimpleTasksDb > Tables**, and
right-click **dbo.Tasks > View Data**. You can also use SSMS and execute:

```sql
USE SimpleTasksDb;
SELECT Id, Title, IsCompleted, CreatedAt
FROM dbo.Tasks
ORDER BY Id DESC;
```

## How the code fits together

| File | Purpose |
| --- | --- |
| `Program.cs` | Registers Razor Pages and the SQL Server database context, initializes the database, and starts the server. |
| `Models/TaskItem.cs` | Describes a task and its database columns. |
| `Data/TaskDbContext.cs` | Gives Entity Framework access to the Tasks table. |
| `Pages/Index.cshtml` | Renders the input form, task list, and action buttons. |
| `Pages/Index.cshtml.cs` | Loads tasks and handles add, toggle, and delete requests. |
| `appsettings.json` | Stores the development database connection string. |

When you submit a form, Razor Pages calls the corresponding `OnPost...`
handler. That handler changes the tracked data and calls `SaveChangesAsync()`
to write the change to SQL Server. Entity Framework generates parameterized SQL.
The page redirects after saving, preventing a browser refresh from repeating
an add operation. Razor encodes displayed task titles, and the form tag helpers
generate antiforgery tokens automatically.

The `Tasks` table has these columns:

| Column | SQL type | Meaning |
| --- | --- | --- |
| `Id` | `int`, primary key, identity | Task identifier |
| `Title` | `nvarchar(100)`, not null | Task text |
| `IsCompleted` | `bit`, not null | Whether the task is complete |
| `CreatedAt` | `datetime2`, not null | Creation time in UTC |

This is a single-user local learning application. All visitors to a running
instance share the same task list. It has no user account system.

## Visual Studio 2022 compatibility

Visual Studio 2022 supports .NET 8 starting at version 17.8. If you need that IDE,
open `SimpleTasks/SimpleTasks.csproj` in a text editor and change:

```xml
<TargetFramework>net8.0</TargetFramework>
```

Also change the Entity Framework package reference to the latest stable 8.0 patch:

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="8.0.*" />
```

Install the .NET 8 SDK if it is missing, then reopen the solution. The application
uses APIs available in both .NET 8 and .NET 10. This compatibility path has not
been compiled here. .NET 8 support ends on November 10, 2026; prefer .NET 10 for
ongoing development. If you use Docker with the .NET 8 variant, also change
both Dockerfile image tags from `10.0` to `8.0`.

## Optional: run with Docker later

The Visual Studio setup above does not require Docker.

For this optional route, use Docker Desktop with Linux containers on an x64
computer and sufficient memory for SQL Server (at least 2 GB for the database
container, plus memory for Docker and the application).

In a PowerShell terminal in the folder containing `compose.yaml`:

```powershell
Copy-Item .env.example .env
notepad .env
```

Replace the example password with your own strong development password and save.
Use uppercase, lowercase, digits, and symbols. Avoid semicolons and double quotes
for this sample's connection-string configuration. Then run:

```powershell
docker compose up --build -d
```

Open http://localhost:5080. Stop the Visual Studio app first if it is using that
port. Compose builds your web application image, starts a separate SQL Server
container, waits for SQL Server to become ready, and overrides the app's LocalDB
connection string with the container connection.

The Docker database is separate from the LocalDB database. A named volume
preserves its data across ordinary container restarts. Stop the containers with:

```powershell
docker compose down
```

Keep the volume if you want to keep the data. Changing the password in `.env`
does not change the password inside an already initialized SQL Server volume.

The Compose configuration uses SQL Server Developer edition for development.
It also sets `ACCEPT_EULA=Y` for the SQL Server container. Database ports are not
published to your host, and the web port is bound to localhost.

## Common setup problems

- **Project cannot target .NET 10:** use Visual Studio 2026 with the .NET 10 SDK,
  or follow the Visual Studio 2022 compatibility steps.
- **LocalDB instance cannot be found:** install SQL Server Express LocalDB via
  Visual Studio Installer. In a Developer PowerShell window, run
  `sqllocaldb info` to list instances and `sqllocaldb start MSSQLLocalDB` to start
  the default instance if it exists.
- **Login or connection error:** use the same SQL Server instance and
  authentication method that already work in SSMS. The defaults assume LocalDB
  with Windows authentication.
- **Port 5080 is busy:** stop the other running instance, or change
  `applicationUrl` in `Properties/launchSettings.json`.
- **NuGet restore fails:** confirm nuget.org is enabled as a package source and
  your computer can reach it.

## Database schema changes

For simplicity, the app uses `EnsureCreatedAsync()`. It initializes a new database
but does not update existing tables after you change the model. Keep the model
as supplied while learning the first version. For later development, introduce
EF Core migrations deliberately rather than mixing them with `EnsureCreatedAsync()`.
The app never drops your existing database automatically.

## Verification status

The project files, configuration files, and archive structure were checked.
The generating environment did not contain the .NET SDK, SQL Server, or Docker,
so the application was not compiled, executed, or tested against SQL Server here.
Use the short check in step 4 after opening it in Visual Studio.

## References

- Razor Pages with SQL Server: https://learn.microsoft.com/en-us/aspnet/core/data/ef-rp/intro?view=aspnetcore-10.0
- Visual Studio and .NET versions: https://learn.microsoft.com/en-us/dotnet/core/install/windows
- EF Core schema initialization: https://learn.microsoft.com/en-us/ef/core/managing-schemas/ensure-created
- SQL Server containers: https://learn.microsoft.com/en-us/sql/linux/install-upgrade/quickstart-install-docker?view=sql-server-ver16

# Simple Tasks

A Jira-style work tracker built as event-driven microservices on .NET 10:
projects with ticket keys (ST-123), a drag-and-drop board, a backlog, labels,
time estimates with work logging, due-date and personal reminders, a live
notification bell, Gmail email, and invite-only Google sign-in.

## Architecture

```text
Browser ──> Web (Razor Pages, BFF) ──REST + JWT──> Identity | Work | Notifications
                    └── SignalR via YARP ───────────────────────────────┘

Identity ──events──> RabbitMQ <──events── Work
                        └──────events──> Notifications ──> Gmail SMTP

SQL Server: SimpleTasks_Identity | SimpleTasks_Work | SimpleTasks_Notifications
```

| Project | Owns |
| --- | --- |
| `SimpleTasks` | The web app: pages, the sign-in cookie, combining data from services. No database. |
| `services/SimpleTasks.Identity` | Users, invites, the Site Admin flag, signing service tokens. |
| `services/SimpleTasks.Work` | Projects, roles, tickets, labels, comments, estimates, work logs, activity. |
| `services/SimpleTasks.Notifications` | The bell, the email queue, reminders, email preferences. |
| `shared/SimpleTasks.Contracts` | Event definitions and the enums they use. |
| `shared/SimpleTasks.Messaging` | Outbox, RabbitMQ publisher, duplicate-safe consumer. |
| `shared/SimpleTasks.ServiceDefaults` | Telemetry, health checks, resilience, token validation. |
| `SimpleTasks.AppHost` | Starts everything locally through .NET Aspire. |

How the parts work together:

- **One database per service.** All three use the server and login from your
  `DefaultConnection`; each service changes only the database name, in code.
- **Events are never lost.** A service saves an event in the same transaction as
  its data, and a background publisher sends it. Consumers ignore duplicates, and
  a message that keeps failing moves to the `simpletasks.dead` queue.
- **Services stay independent.** If Notifications is down, tickets keep working,
  the bell shows as unavailable, and queued events catch up when it returns.

## Run it

You need the .NET 10 SDK, Docker Desktop, and SQL Server with the login in your
connection string.

1. If `SimpleTasks/appsettings.json` doesn't exist, copy
   `SimpleTasks/appsettings.example.json` to that name and set your connection
   string. The file is git-ignored.
2. Set the first Site Admin:

```bash
dotnet user-secrets set FirstAdminEmail "you@gmail.com" --project SimpleTasks.AppHost
```

3. In Visual Studio, set **SimpleTasks.AppHost** as the startup project and press
   F5. From a terminal, run:

```bash
dotnet run --project SimpleTasks.AppHost --launch-profile SimpleTasks.AppHost
```

The app opens at http://localhost:5080, and the Aspire dashboard with logs and
request traces at http://localhost:15080. In Development, the sign-in page offers
test accounts. To use real Google sign-in and Gmail, see
[docs/setup-google.md](docs/setup-google.md).

To try things without touching your real databases, start a throwaway run with
its own temporary SQL Server and RabbitMQ:

```bash
dotnet run --project SimpleTasks.AppHost --launch-profile SimpleTasks.AppHost -- UseContainerSql=true
```

On first start, the Work service copies the rows from your original `Tasks` table
into a project called Simple Tasks (key `ST`). This happens once. The original
database is only read, so it stays as a fallback.

## Run with Docker Compose

Compose runs its own SQL Server, separate from any server you already have.

```powershell
Copy-Item .env.example .env
notepad .env
```

```bash
docker compose up --build -d
```

Open http://localhost:5080. Compose runs in Production mode, so there's no test
sign-in: set up Google first using [docs/setup-google.md](docs/setup-google.md).

## Run the tests

Docker Desktop must be running. The integration and end-to-end tests use
throwaway containers and never touch your real databases.

```bash
dotnet test --project tests/SimpleTasks.UnitTests
```

```bash
dotnet test --project tests/SimpleTasks.IntegrationTests
```

```bash
dotnet test --project tests/SimpleTasks.EndToEndTests
```

## Add a database change

Each service has its own migrations. For example:

```bash
dotnet ef migrations add AddSomething --project services/SimpleTasks.Work --context WorkDb -o Data/Migrations
```

Services apply their migrations when they start.

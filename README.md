# Simple Tasks

A Jira-style work tracker built as event-driven microservices on .NET 10:
projects with ticket keys (ST-123), a drag-and-drop board, a backlog, labels,
time estimates with work logging, due-date and personal reminders, a live
notification bell, Gmail email, and invite-only email and password login.

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
| `services/SimpleTasks.Identity` | Users, passwords, invites and reset links, the Site Admin flag, signing service tokens. |
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
request traces at http://localhost:15080. In Development, the login page also
offers test accounts that need no password.

To log in as yourself the first time, open the link the app emails you, or copy it
from the Identity logs in the dashboard. Everyone else joins by invite. To send real
email, see [docs/setup-email.md](docs/setup-email.md).

To try things without touching your real databases, start a throwaway run with
its own temporary SQL Server and RabbitMQ:

```bash
dotnet run --project SimpleTasks.AppHost --launch-profile SimpleTasks.AppHost -- UseContainerSql=true
```

On first start, the Work service copies the rows from your original `Tasks` table
into a project called Simple Tasks (key `ST`). This happens once. The original
database is only read, so it stays as a fallback.

## Use it on your Wi-Fi

By default only this computer can open the app. To let people on the same network
use it, set `PublicUrl` to this computer's address on that network. Invite and
reset links then use that address too.

1. Find this computer's IPv4 address with `ipconfig`, for example `192.168.1.6`.
2. Set it, with port 5080:

```bash
dotnet user-secrets set PublicUrl "http://192.168.1.6:5080" --project SimpleTasks.AppHost
```

3. In Windows, set the Wi-Fi network to **Private**. Public networks block incoming
   connections whatever the firewall says.
4. In an administrator PowerShell, allow the port on private networks only:

```powershell
New-NetFirewallRule -DisplayName "Simple Tasks" -Direction Inbound -Protocol TCP -LocalPort 5080 -Action Allow -Profile Private
```

5. Restart the app. Others open `http://192.168.1.6:5080`.

Only the web app is opened to the network. The services, the dashboard, and the
passwordless test accounts stay on this computer. Traffic isn't encrypted, so use
this on a network you trust. If your router gives this computer a different address
later, update `PublicUrl`; a DHCP reservation in the router keeps it fixed.

## Run with Docker Compose

Compose builds an image for each service (`simpletasks-identity`, `-work`,
`-notifications` and `-web`) and runs them with RabbitMQ. By default they use the
SQL Server already on this computer, so you keep your data. Stop the Visual Studio
run first, because both use port 5080.

1. Create your settings file. Put in the same SQL password as
   `SimpleTasks/appsettings.json`, and set `PUBLIC_URL` to the address people will open:

```powershell
Copy-Item .env.example .env
notepad .env
```

2. Build and start everything:

```bash
docker compose up --build -d
```

Open the address in `PUBLIC_URL`. Compose runs in Production mode, so there are no
test accounts. Get your first password link from your email, or from the Identity
logs; see [docs/setup-email.md](docs/setup-email.md):

```bash
docker compose logs identity
```

The containers restart on their own whenever Docker starts. Useful commands:

```bash
docker compose ps
```

```bash
docker compose down
```

After changing code, rebuild and restart with `docker compose up --build -d`. To run
a separate SQL Server inside Compose instead, set `SQL_SERVER=db` in `.env` and add
`--profile own-db` to the `up` command; that database starts empty.

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

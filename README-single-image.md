# Simple Tasks: one image with the web app and SQL Server

This add-on builds one Linux Docker image containing your existing Simple Tasks
web application, its .NET 10 runtime, and SQL Server 2025 Developer edition.
One container runs both services. The startup script waits for SQL Server to
accept connections, starts the web app, and stops both services when the
container receives its stop signal or either service exits.

The database name is `SimpleTasks`. The application creates the database and
its Tasks table on first launch, using the initialization code already in your
project. Database rows live in a persistent Docker volume.

## Requirements

- Your existing, working SimpleTasks project targeting .NET 10.
- Docker Desktop using Linux containers on an x64 computer.
- Enough memory for both SQL Server and the app. SQL Server requires at least
  2 GB; allocate additional memory for Docker and the web app.
- Internet access during the first build to obtain the base images, NuGet
  packages, and Linux runtime dependencies.

The normal Docker design is one service per container, often managed with
Compose. This add-on implements your requested single-image design for a local
learning project.

## 1. Put the add-on files beside your solution

Extract this add-on and copy these files into your existing `SimpleTasksStarter`
folder, beside `SimpleTasks.sln`:

- `Dockerfile.single-image`
- `start-single-image.sh`
- `.env.single-image.example`
- `.dockerignore`
- `README-single-image.md`

The expected locations are:

| File | Location relative to your solution folder |
| --- | --- |
| Solution | `SimpleTasks.sln` |
| Existing application project | `SimpleTasks/SimpleTasks.csproj` |
| New Dockerfile | `Dockerfile.single-image` |
| New startup script | `start-single-image.sh` |

Copy the supplied `.dockerignore` as well. It excludes your local connection
settings and environment files from the Docker build context. The container
receives its database connection configuration when it starts.

## 2. Choose the container's password

Open PowerShell in that solution folder and run:

```powershell
Copy-Item .env.single-image.example .env.single-image
notepad .env.single-image
```

Replace `REPLACE_WITH_YOUR_STRONG_PASSWORD` with your chosen password. Use at
least 12 characters with uppercase and lowercase letters, numbers, and symbols.
Enter the password without surrounding quotes. This file is read by
`docker run --env-file`, which preserves the literal value rather than using
Compose's variable interpolation rules.

The file sets `ACCEPT_EULA=Y` for the SQL Server license terms and uses Developer
edition for development. Keep the environment file local.

Your existing Visual Studio connection string can stay as it is. The startup
script supplies `ConnectionStrings__DefaultConnection` inside this container.
Both processes are in this same container, so SQL Server is reached at
`localhost,1433` internally.

## 3. Build the image

Run from the folder containing `SimpleTasks.sln` and the new Dockerfile:

```powershell
docker build -f Dockerfile.single-image -t simpletasks-single:latest .
```

The first build downloads large images and can take several minutes. It
compiles the application in a .NET SDK build stage, then copies the published
application and its runtime into the SQL Server image. You do not need to
install an ASP.NET runtime inside the final image separately.

## 4. Start one container

Stop the copy of the web app running in Visual Studio so port 5080 is free.
Then run this as one PowerShell command:

```powershell
docker run -d --name simpletasks-single --init --stop-timeout 30 --env-file .env.single-image -p 127.0.0.1:5080:8080 -v simpletasks-single-data:/var/opt/mssql simpletasks-single:latest
```

Open http://localhost:5080 after SQL Server finishes its initial startup.
To follow progress:

```powershell
docker logs -f simpletasks-single
```

Press Ctrl+C to leave the log viewer; this does not stop the container. You can
check its status with `docker ps`.

The container runs as the SQL Server image's non-root `mssql` user. The readiness
check passes the password to sqlcmd through its environment. The script handles
password values containing semicolons or double quotes when building the
application's connection string.

## 5. Stop and start it again

Stop:

```powershell
docker stop simpletasks-single
```

Start that same container again:

```powershell
docker start simpletasks-single
```

The named `simpletasks-single-data` volume preserves the database across
container recreation. `docker run` creates a new container; after the first run,
use `docker start` to start the existing named container.

For changes to C# or Razor files, rebuild the image. Stop and remove only this
app container, then repeat the run command with the same volume name:

```powershell
docker stop simpletasks-single
docker rm simpletasks-single
docker build -f Dockerfile.single-image -t simpletasks-single:latest .
docker run -d --name simpletasks-single --init --stop-timeout 30 --env-file .env.single-image -p 127.0.0.1:5080:8080 -v simpletasks-single-data:/var/opt/mssql simpletasks-single:latest
```

Removing the container in these commands preserves the named volume.
Use the same SQL password when reusing that database volume. Changing the
environment file does not reset an existing database login password.

## Your current Docker SQL Server database

This creates a separate SQL Server instance and a fresh database volume. It
does not import the rows from the SQL Server container you currently use.
Your current container can stay running because the new container does not
publish a SQL Server host port.

To transfer your current task rows, back up the current `SimpleTasks` database
and restore that backup to the new instance. Keep the current database until
that transfer is verified. The image and the database volume are separate:
exporting the image does not export rows from either volume.

If you want to access the new instance from SSMS, add the following argument
before `simpletasks-single:latest` in the initial run command:

```text
-p 127.0.0.1:14330:1433
```

Then connect to `localhost,14330`, with SQL Server Authentication, login `sa`,
and the password from your environment file. Select Trust Server Certificate
for this local development instance. Use another host port if 14330 is busy.

## Export the software image to another computer

```powershell
docker save -o simpletasks-single-image.tar simpletasks-single:latest
```

On the other x64 computer with Docker using Linux containers:

```powershell
docker load -i simpletasks-single-image.tar
```

Supply an environment file there and run the same `docker run` command. A new
volume starts with an empty task list. Transfer a database backup separately
if you also want the existing rows.

## Verification

The Dockerfile and archive structure were inspected, the startup script passed
Bash syntax checks, and its process startup/shutdown behavior was checked with
stub processes. The generating environment has no Docker, .NET SDK, or SQL
Server runtime, so the real image build and SQL Server integration remain
unverified. Docker Desktop on your computer performs the actual build.

## References

- SQL Server image and prerequisites: https://learn.microsoft.com/en-us/sql/linux/install-upgrade/quickstart-install-docker?view=sql-server-ver17
- Managing multiple container processes: https://docs.docker.com/engine/containers/multi-service_container/
- .NET native runtime dependencies: https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install
- Docker volumes: https://docs.docker.com/engine/storage/volumes/

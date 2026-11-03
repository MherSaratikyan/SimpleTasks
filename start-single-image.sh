#!/usr/bin/env bash
set -Eeuo pipefail

: "${MSSQL_SA_PASSWORD:?Set MSSQL_SA_PASSWORD in your environment file.}"
if [[ "${ACCEPT_EULA:-}" != "Y" && "${ACCEPT_EULA:-}" != "y" ]]; then
    echo "Set ACCEPT_EULA=Y to accept the SQL Server license terms." >&2
    exit 1
fi

sqlcmd_path=""
for candidate in /opt/mssql-tools18/bin/sqlcmd /opt/mssql-tools/bin/sqlcmd; do
    if [[ -x "$candidate" ]]; then
        sqlcmd_path="$candidate"
        break
    fi
done
if [[ -z "$sqlcmd_path" ]]; then
    echo "The SQL Server image does not contain the sqlcmd readiness utility." >&2
    exit 1
fi

# Quote and escape values for the SqlClient connection string.
# This keeps semicolons and double quotes inside the password value.
connection_password="${MSSQL_SA_PASSWORD//\"/\"\"}"
connection_database="${SIMPLETASKS_DATABASE:-SimpleTasks}"
connection_database="${connection_database//\"/\"\"}"
export ConnectionStrings__DefaultConnection="Server=localhost,1433;Database=\"${connection_database}\";User Id=sa;Password=\"${connection_password}\";Encrypt=True;TrustServerCertificate=True"

sql_pid=""
app_pid=""

stop_children() {
    trap - EXIT TERM INT
    for child_pid in "$app_pid" "$sql_pid"; do
        if [[ -n "$child_pid" ]]; then
            kill -TERM "$child_pid" 2>/dev/null || true
        fi
    done
    for child_pid in "$app_pid" "$sql_pid"; do
        if [[ -n "$child_pid" ]]; then
            wait "$child_pid" 2>/dev/null || true
        fi
    done
}

trap stop_children EXIT
trap 'exit 143' TERM
trap 'exit 130' INT

echo "Starting SQL Server..."
/opt/mssql/bin/sqlservr &
sql_pid=$!

database_ready=false
for ((attempt = 1; attempt <= 120; attempt++)); do
    if ! kill -0 "$sql_pid" 2>/dev/null; then
        echo "SQL Server exited before it was ready. Check the container logs." >&2
        wait "$sql_pid" 2>/dev/null || true
        exit 1
    fi

    if SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" "$sqlcmd_path" \
        -S tcp:127.0.0.1,1433 -U sa -C -b -l 2 -t 2 \
        -Q "SELECT 1" -o /dev/null > /dev/null 2>&1; then
        database_ready=true
        break
    fi
    sleep 1
done

if [[ "$database_ready" != true ]]; then
    echo "SQL Server did not become ready. Check its logs and the password for this data volume." >&2
    exit 1
fi

echo "SQL Server is ready. Starting Simple Tasks on port 8080..."
/app/SimpleTasks &
app_pid=$!

# If either service stops, stop its companion and return that service's status.
set +e
wait -n "$sql_pid" "$app_pid"
exit_code=$?
set -e
exit "$exit_code"

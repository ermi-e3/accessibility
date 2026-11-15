param(
    [Parameter(Mandatory)] [string]$AdminConnectionString,
    [Parameter(Mandatory)] [string]$HangfireOwnerUser,
    [Parameter(Mandatory)] [string]$HangfireOwnerPassword,
    [Parameter(Mandatory)] [string]$HangfireRuntimeUser,
    [Parameter(Mandatory)] [string]$HangfireRuntimePassword,
    [string]$PsqlPath = "psql"
)

$ErrorActionPreference = "Stop"
$builder = [Data.Common.DbConnectionStringBuilder]::new()
$builder.ConnectionString = $AdminConnectionString
$hostName = [string]$builder["Host"]
$port = if ($builder.ContainsKey("Port")) { [string]$builder["Port"] } else { "5432" }
$database = [string]$builder["Database"]
$adminUser = [string]$builder["Username"]
$env:PGPASSWORD = [string]$builder["Password"]
try {
    $sql = @"
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'owner_user', :'owner_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'owner_user') \gexec
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'runtime_user', :'runtime_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'runtime_user') \gexec
SELECT format('ALTER ROLE %I PASSWORD %L', :'owner_user', :'owner_password') \gexec
SELECT format('ALTER ROLE %I PASSWORD %L', :'runtime_user', :'runtime_password') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), :'owner_user') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), :'runtime_user') \gexec
SELECT format('CREATE SCHEMA IF NOT EXISTS hangfire AUTHORIZATION %I', :'owner_user') \gexec
SELECT format('ALTER SCHEMA hangfire OWNER TO %I', :'owner_user') \gexec
SELECT format('GRANT USAGE ON SCHEMA hangfire TO %I', :'runtime_user') \gexec
SELECT format(
  'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA hangfire GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I',
  :'owner_user', :'runtime_user') \gexec
SELECT format(
  'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA hangfire GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO %I',
  :'owner_user', :'runtime_user') \gexec
"@
    $sql | & $PsqlPath `
        --host $hostName `
        --port $port `
        --username $adminUser `
        --dbname $database `
        --set ON_ERROR_STOP=1 `
        --set "owner_user=$HangfireOwnerUser" `
        --set "owner_password=$HangfireOwnerPassword" `
        --set "runtime_user=$HangfireRuntimeUser" `
        --set "runtime_password=$HangfireRuntimePassword"
    if ($LASTEXITCODE -ne 0) { throw "Hangfire role/schema provisioning failed." }
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}

"Hangfire identities/schema provisioned. Initialize once with the owner connection and schema preparation enabled, then use the runtime connection with preparation disabled."

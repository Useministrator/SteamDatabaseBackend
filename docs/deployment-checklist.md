# Deployment Checklist

Recorded: 2026-04-08

## Purpose

This checklist is for deploying SteamDatabaseBackend on:
- Ubuntu with `systemd`
- Windows with a console or a scheduled/background run

It includes the database import step from [_database.sql](/C:/git/SteamDatabaseBackend/_database.sql).

## Common Prerequisites

- .NET 10 SDK installed on the build machine
- MySQL 8.4 LTS or MariaDB installed and reachable from the backend host
- a Steam account for the backend
- a `settings.json` prepared from [settings.json.default](/C:/git/SteamDatabaseBackend/settings.json.default)

Recommended defaults:
- `LogToFile = false`
- `LogLevel = "Info"`
- `SteamKitDebugLogEnabled = false`
- `IRC.Enabled = false` until the base deployment is confirmed working

## Database Checklist

### 1. Create the database

Example:

```sql
CREATE DATABASE steamdb CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;
CREATE USER 'steamdb'@'localhost' IDENTIFIED BY 'change-me';
GRANT ALL PRIVILEGES ON steamdb.* TO 'steamdb'@'localhost';
FLUSH PRIVILEGES;
```

### 2. Import the schema from `_database.sql`

Ubuntu example:

```bash
mysql -uroot -p steamdb < /path/to/SteamDatabaseBackend/_database.sql
```

Windows example:

```powershell
Get-Content 'C:\path\to\SteamDatabaseBackend\_database.sql' |
  & 'C:\Program Files\MySQL\MySQL Server 8.4\bin\mysql.exe' -uroot -p steamdb
```

### 3. Verify the schema import

Minimal checks:
- database exists
- key tables exist: `Apps`, `Subs`, `Depots`, `LocalConfig`

Ubuntu example:

```bash
mysql -uroot -p -D steamdb -e "SHOW TABLES LIKE 'Apps'; SHOW TABLES LIKE 'LocalConfig';"
```

Windows example:

```powershell
& 'C:\Program Files\MySQL\MySQL Server 8.4\bin\mysql.exe' -uroot -p -D steamdb -e "SHOW TABLES LIKE 'Apps'; SHOW TABLES LIKE 'LocalConfig';"
```

## Ubuntu Checklist

### 1. Publish the backend

On the build machine:

```bash
dotnet publish SteamDatabaseBackend.csproj -c Release --framework net10.0 --runtime linux-x64 --self-contained true -p:PublishSingleFile=true
```

### 2. Create the target directory

```bash
sudo install -d -m 0755 /opt/steamdatabasebackend
```

### 3. Copy the published files

```bash
sudo cp -r bin/linux-x64/publish/* /opt/steamdatabasebackend/
```

### 4. Create `settings.json`

```bash
sudo cp /opt/steamdatabasebackend/settings.json.default /opt/steamdatabasebackend/settings.json
sudo editor /opt/steamdatabasebackend/settings.json
```

Checklist for `settings.json`:
- correct DB connection string
- correct Steam username and password
- `BuiltInHttpServerPort` set if local HTTP endpoints are needed
- `LogToFile = false`
- `LogLevel = "Info"`
- `SteamKitDebugLogEnabled = false`

### 5. Create the service account

```bash
sudo useradd --system --home /opt/steamdatabasebackend --shell /usr/sbin/nologin steamdb
sudo chown -R steamdb:steamdb /opt/steamdatabasebackend
```

### 6. Install the `systemd` unit

```bash
sudo cp contrib/systemd/steamdatabasebackend.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable steamdatabasebackend
```

### 7. First start

For the very first login, choose one of these paths:
- easiest: start manually once in a shell if you expect interactive Steam Guard
- headless: create `/run/steamdatabasebackend/steam-guard.env` before service start

Example one-time guard file:

```bash
sudo install -d -m 0750 -o steamdb -g steamdb /run/steamdatabasebackend
printf 'STEAM_GUARD_CODE=ABCDE\n' | sudo tee /run/steamdatabasebackend/steam-guard.env > /dev/null
sudo chown steamdb:steamdb /run/steamdatabasebackend/steam-guard.env
sudo chmod 0600 /run/steamdatabasebackend/steam-guard.env
```

Then start:

```bash
sudo systemctl start steamdatabasebackend
```

### 8. Verify the service

```bash
systemctl status steamdatabasebackend
journalctl -u steamdatabasebackend -n 100 --no-pager
```

Healthy signs:
- `Connected, logging in...`
- `Logged in, current Valve time is ...`
- `WebAuth: Authenticated`
- no restart loop

### 9. Verify the HTTP endpoint if enabled

```bash
curl http://localhost:8081/Debug
```

### 10. Verify token persistence

After the first successful login:
- `LocalConfig` should contain `backend.loginkey`
- future service restarts should not require a fresh Steam Guard code unless Steam invalidates the token

## Windows Checklist

### 1. Publish the backend

On the build machine:

```powershell
dotnet publish .\SteamDatabaseBackend.csproj -c Release --framework net10.0 --runtime win-x64 --self-contained true -p:PublishSingleFile=true
```

### 2. Create the target directory

Example:

```powershell
New-Item -ItemType Directory -Force C:\SteamDatabaseBackend | Out-Null
```

### 3. Copy the published files

```powershell
Copy-Item .\bin\win-x64\publish\* C:\SteamDatabaseBackend\ -Recurse -Force
```

### 4. Create `settings.json`

```powershell
Copy-Item C:\SteamDatabaseBackend\settings.json.default C:\SteamDatabaseBackend\settings.json
notepad C:\SteamDatabaseBackend\settings.json
```

Checklist for `settings.json`:
- correct DB connection string
- correct Steam username and password
- `BuiltInHttpServerPort` set if local HTTP endpoints are needed
- `LogToFile = false`
- `LogLevel = "Info"`
- `SteamKitDebugLogEnabled = false`

### 5. First run

Recommended for the first start:
- run the binary in an interactive PowerShell window once
- complete Steam Guard if requested
- verify that `backend.loginkey` is saved in `LocalConfig`

Example:

```powershell
Set-Location C:\SteamDatabaseBackend
.\SteamDatabaseBackend.exe -f=ImportantOnly
```

### 6. Optional non-interactive Steam Guard

If you do not want to enter the code interactively, set a one-time environment variable in the same PowerShell session before the start:

```powershell
$env:STEAM_GUARD_CODE = 'ABCDE'
.\SteamDatabaseBackend.exe -f=ImportantOnly
Remove-Item Env:\STEAM_GUARD_CODE
```

### 7. Verify the run

Healthy signs:
- `Connected, logging in...`
- `Logged in, current Valve time is ...`
- `WebAuth: Authenticated`

If HTTP is enabled:

```powershell
Invoke-WebRequest http://localhost:8081/Debug | Select-Object -ExpandProperty Content
```

### 8. Optional background execution

This repository does not ship a native Windows service wrapper.

Common practical options:
- Task Scheduler
- NSSM or another external service wrapper
- a dedicated PowerShell startup script

If you use a wrapper, keep these rules:
- working directory must be the publish directory
- `settings.json` must stay next to the executable
- logs are easier to manage with `LogToFile = false` and external stdout/stderr capture

## Post-Deployment Smoke Checklist

After deployment on either OS:
- backend process is running
- DB connection succeeds
- Steam login succeeds
- `backend.loginkey` exists in `LocalConfig`
- `WebAuth: Authenticated` appears in logs
- `/Debug` responds if HTTP is enabled
- no rapid reconnect loop

## Recovery Checklist

If Steam asks for a new guard code later:

Ubuntu:
- create `/run/steamdatabasebackend/steam-guard.env`
- restart the service
- the backend will consume the file once and delete it

Windows:
- set a one-time environment variable in the current shell
- start the process again

If the stored login token becomes invalid:
- the backend should clear the stale token
- it should back off before reconnecting
- it should request a fresh Steam Guard code instead of hammering login attempts

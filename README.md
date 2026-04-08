# Steam Database Backend

The application that keeps [SteamDB](https://steamdb.info/) up to date with the latest changes directly from Steam,
additionally it runs an IRC bot and announces various Steam stuff in #steamdb and #steamdb-announce on [Freenode](https://freenode.net/).

This source code is provided as-is for reference. It is highly tuned for SteamDB's direct needs and is not a generic application.
If you plan on running this yourself, keep in mind that we won't provide support for it.

## Requirements
* [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
* [MySQL](https://www.mysql.com/) or [MariaDB](https://mariadb.org/) server

## Installation
1. Import `_database.sql` to your database
2. Copy `settings.json.default` to `settings.json`
3. Edit `settings.json` as needed: database connection string and Steam credentials.

Use `anonymous` as the Steam username if you need to debug quickly.

Detailed deployment steps for Ubuntu and Windows are in `docs/deployment-checklist.md`.

## systemd
For Linux deployments, a recommended unit file is available at `contrib/systemd/steamdatabasebackend.service`.

Suggested operational defaults:
* Set `LogToFile` to `false` and use `journalctl` for logs
* Keep `LogLevel` at `Info` for regular service use, or raise it to `Warn` if you only want warnings and errors
* Keep `SteamKitDebugLogEnabled` at `false` unless you are actively debugging Steam connectivity
* Keep `settings.json` next to the published binary
* Use `/run/steamdatabasebackend/steam-guard.env` for one-time Steam Guard codes during first login or token recovery

Example:
```bash
sudo install -d -m 0755 /opt/steamdatabasebackend
sudo cp -r publish/* /opt/steamdatabasebackend/
sudo cp contrib/systemd/steamdatabasebackend.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now steamdatabasebackend
```

When Steam asks for a new guard code, create `/run/steamdatabasebackend/steam-guard.env` with one of these variables and restart the service:
```bash
STEAM_GUARD_CODE=ABCDE
STEAM_EMAIL_CODE=ABCDE
STEAM_TWO_FACTOR_CODE=ABCDE
```

The backend reads this file directly on startup or token recovery and deletes it after consuming a code. This makes the file suitable for one-time Steam Guard codes without reusing stale values on the next reconnect.

## License
Use of SteamDB Updater is governed by a BSD-style license that can be found in the LICENSE file.

# Steam Database Backend Reference

This document describes the repository as it exists today. It is intended to be the main technical reference for:

- architecture and runtime behavior
- `settings.json` configuration
- startup parameters and full-run modes
- database schema and runtime keys stored in the database
- built-in HTTP routes
- IRC and Steam chat command surface
- operational notes for Windows and Linux deployments

## 1. What This Backend Does

`SteamDatabaseBackend` is a long-running .NET service that keeps a SteamDB-style database up to date from Steam itself.

At a high level it:

- logs in to Steam through SteamKit
- polls or enumerates PICS product changes
- stores app, package, changelist, depot, token, and history data in MySQL/MariaDB
- optionally downloads selected files from depots
- optionally runs an IRC bot
- optionally polls RSS feeds and writes RSS state into the database
- optionally exposes a small localhost-only HTTP API for debugging and data access

This is not a generic framework. The codebase is strongly shaped around SteamDB's own data model and workflows.

## 2. Current Technical State

The current codebase targets:

- `.NET`: `net10.0`
- database connector: `MySqlConnector 2.5.0`
- ORM: `Dapper 2.1.72`
- JSON: `Newtonsoft.Json 13.0.4`
- Steam client stack: `SteamKit2 3.4.0`
- IRC client: `NetIrc2 1.0.0`

Main entrypoint:

- `Bootstrapper.Main()` in `/C:/git/SteamDatabaseBackend/Bootstrapper.cs`

Primary startup path:

1. Parse command-line arguments.
2. Load `settings.json` from the executable directory.
3. Validate configuration and database connectivity.
4. Initialize shared runtime state in `Application.Init()`.
5. Start Steam and enter the callback loop.

Important runtime directories relative to the executable:

- `settings.json`
- `logs/`
- `app/`
- `sub/`
- `ugc/`
- `files/`

## 3. High-Level Architecture

### Core bootstrap and lifecycle

- `/C:/git/SteamDatabaseBackend/Bootstrapper.cs`
  - parses startup arguments
  - loads settings
  - configures logging
  - registers `SIGINT` and `SIGTERM`
  - starts the Steam callback loop
- `/C:/git/SteamDatabaseBackend/Application.cs`
  - loads important app/package lists
  - loads cached PICS tokens and key-name caches
  - optionally starts the built-in HTTP server
  - in normal daemon mode wires IRC and RSS
  - owns shutdown/cleanup ordering

### Steam integration

- `/C:/git/SteamDatabaseBackend/Steam/Steam.cs`
  - central singleton for SteamKit handlers and Steam-related services
- `/C:/git/SteamDatabaseBackend/Steam/Connection.cs`
  - connection lifecycle, login, token recovery, Steam Guard handling, reconnect backoff
- `/C:/git/SteamDatabaseBackend/Steam/PICSChanges.cs`
  - changelist polling and incremental update scheduling
- `/C:/git/SteamDatabaseBackend/Steam/PICSProductInfo.cs`
  - routes product info responses into processors
- `/C:/git/SteamDatabaseBackend/Steam/PICSTokens.cs`
  - token storage and token refresh pipeline
- `/C:/git/SteamDatabaseBackend/Steam/LicenseList.cs`
  - owned apps, packages, and depot ownership cache
- `/C:/git/SteamDatabaseBackend/Steam/WebAuth.cs`
  - Steam web-session bootstrap from refresh tokens
- `/C:/git/SteamDatabaseBackend/Processors/DepotProcessor.cs`
  - depot manifests, depot history, depot files, depot keys, and optional file downloads

### Database processors

- `/C:/git/SteamDatabaseBackend/Processors/AppProcessor.cs`
  - updates `Apps`, `AppsInfo`, `AppsHistory`, triggers depot processing
- `/C:/git/SteamDatabaseBackend/Processors/SubProcessor.cs`
  - updates `Subs`, `SubsInfo`, `SubsApps`, `SubsHistory`
- `/C:/git/SteamDatabaseBackend/Processors/FullUpdateProcessor.cs`
  - batch and maintenance runs
- `/C:/git/SteamDatabaseBackend/Processors/KeyNameCache.cs`
  - dynamic key registry for app/package key names

### External interfaces

- `/C:/git/SteamDatabaseBackend/Managers/HttpServer.cs`
  - localhost HTTP API
- `/C:/git/SteamDatabaseBackend/IRC/IRC.cs`
  - IRC connection management
- `/C:/git/SteamDatabaseBackend/IRC/CommandHandler.cs`
  - IRC and Steam chat command router
- `/C:/git/SteamDatabaseBackend/IRC/RSS.cs`
  - RSS polling and optional patch-note enrichment

### Operational helpers

- `/C:/git/SteamDatabaseBackend/Managers/Watchdog.cs`
  - reconnect and refresh watchdog
- `/C:/git/SteamDatabaseBackend/Util/Log.cs`
  - console/file logging with levels
- `/C:/git/SteamDatabaseBackend/Util/LocalConfig.cs`
  - key/value persistence in `LocalConfig`
- `/C:/git/SteamDatabaseBackend/Database/Database.cs`
  - MySQL/MariaDB connection factory

## 4. Runtime Modes

The backend has two major execution styles:

- normal daemon mode
- full-run mode

### Normal daemon mode

This is the default when the process is started without `-f`.

Behavior:

- starts Steam and keeps a persistent session
- loads the previous changelist from `LocalConfig`
- starts incremental PICS polling
- optionally runs IRC
- optionally runs RSS polling
- optionally runs the localhost HTTP server

This is the recommended service mode for Windows services, `systemd`, and long-lived deployments.

### Full-run mode

Any `-f=<state>` switch puts the process into full-run mode.

Behavioral differences:

- file logging is forcibly disabled
- IRC is disabled
- RSS is not started
- the normal PICS incremental ticker is not started
- after login, the backend performs a one-off batch workflow based on the selected full-run state

This mode is intended for maintenance, backfills, or one-shot syncs.

## 5. Startup Parameters

The only supported command-line parameter is:

```text
-f=<FullRunState>
```

Unknown arguments cause startup failure.

Valid `FullRunState` values are defined in `/C:/git/SteamDatabaseBackend/Util/FullRunState.cs`:

- `None`
- `Normal`
- `WithForcedDepots`
- `ImportantOnly`
- `Enumerate`
- `PackagesNormal`
- `TokensOnly`
- `NormalUsingMetadata`

### Full-run states

| State | What it does |
| --- | --- |
| `None` | Normal daemon mode. |
| `Normal` | Full refresh of known app and package universe, including token requests. |
| `WithForcedDepots` | App-focused full refresh that also reprocesses depots more aggressively instead of skipping already-known manifests. |
| `ImportantOnly` | Refresh only IDs listed in `ImportantApps` and `ImportantSubs`. |
| `Enumerate` | Enumerate app/sub IDs from `0` up to the current max ID plus a buffer, then request metadata for all of them. |
| `PackagesNormal` | Refresh packages without the full app list pass. |
| `TokensOnly` | Re-query product info only for IDs that already have stored PICS tokens. |
| `NormalUsingMetadata` | Metadata-only pass first, then request full tokens only for changed apps/packages. |

### Examples

Normal daemon mode:

```bash
./SteamDatabaseBackend
```

Important-only maintenance run:

```bash
./SteamDatabaseBackend -f=ImportantOnly
```

Full metadata-first maintenance run:

```bash
./SteamDatabaseBackend -f=NormalUsingMetadata
```

## 6. `settings.json` Reference

The backend loads `settings.json` from the executable directory. Missing required fields fail startup because the JSON model uses strict required-property validation.

Reference file:

- `/C:/git/SteamDatabaseBackend/settings.json.default`

Loader and validation:

- `/C:/git/SteamDatabaseBackend/Util/Settings.cs`
- `/C:/git/SteamDatabaseBackend/Util/SettingsJson.cs`

### Top-level settings

| Key | Type | Required | Default | Purpose |
| --- | --- | --- | --- | --- |
| `ConnectionString` | `string` | yes | none | MySQL/MariaDB connection string used by `MySqlConnector`. |
| `RawBaseURL` | `uri` | yes | none | Base URL for raw dump links returned by app/sub/ugc commands. |
| `BaseURL` | `uri` | yes | none | Base URL for generated public links, IRC realname, and SteamDB-style pages. |
| `WebhookURL` | `uri` or empty | no | `null` | Optional JSON webhook target for selected events. |
| `LogToFile` | `bool` | yes | none | When `true`, log lines are also written under `logs/`. |
| `LogLevel` | `Debug` / `Info` / `Warn` / `Error` | no | `Info` | Minimum application log level. |
| `SteamKitDebugLogEnabled` | `bool` | no | `false` | Enables SteamKit low-level network/debug logging. |
| `OnlyOwnedDepots` | `bool` | yes | none | If `true`, depot manifest downloads are limited to depots owned by the logged-in account. |
| `BuiltInHttpServerPort` | `uint` | yes | none | Port for the built-in localhost HTTP API. `0` disables the listener. |
| `RssFeeds` | `array<uri>` | yes | `[]` | RSS feeds to poll every minute. Empty disables RSS. |
| `Steam` | object | yes | none | Steam credentials and API key. |
| `IRC` | object | yes | none | IRC connectivity and channel configuration. |

### `Steam`

| Key | Type | Required | Purpose |
| --- | --- | --- | --- |
| `Steam.Username` | `string` | yes | Steam account username. `"anonymous"` is supported for limited debugging scenarios. |
| `Steam.Password` | `string` | yes | Steam account password. Still required by JSON validation even when using anonymous login in the sample file. |
| `Steam.WebAPIKey` | `string` | yes | Web API key used for WebAPI-backed calls such as vanity URL resolution and app-list queries. |

Notes:

- If `Steam.Username` is `"anonymous"`, the backend logs in anonymously and skips credential-based login.
- Anonymous mode is useful for quick smoke tests but limits access to owned licenses, depots, and authenticated web actions.

### `IRC`

| Key | Type | Required | Purpose |
| --- | --- | --- | --- |
| `IRC.Enabled` | `bool` | yes | Enables IRC mode if the rest of the IRC configuration is valid and the process is not in full-run mode. |
| `IRC.Ssl` | `bool` | yes | Enables TLS for the IRC connection. |
| `IRC.Server` | `string` | yes | IRC hostname. |
| `IRC.Port` | `int` | yes | IRC port. |
| `IRC.Nickname` | `string` | yes | IRC nickname used for login. |
| `IRC.Password` | `string` | yes | IRC NickServ/server password. Empty string is treated as `null`. |
| `IRC.CommandPrefix` | `char` | yes | Prefix for both IRC commands and Steam friend-chat commands. |
| `IRC.Channel.Ops` | `string` | yes | Operator channel. Also used for some bot-only actions and alerts. |
| `IRC.Channel.Announce` | `string` | yes | Public announcement channel. |

Important behavior:

- IRC is forcibly disabled in any full-run mode.
- IRC is also disabled if server, port, or nickname are invalid.
- The same command prefix is used for Steam friend-chat commands.

### Logging behavior

`/C:/git/SteamDatabaseBackend/Util/Log.cs` implements the following categories:

- `DEBUG`
- `INFO`
- `WARN`
- `ERROR`
- `STEAMKIT`

Filtering behavior:

- `LogLevel=Debug` shows everything except SteamKit unless `SteamKitDebugLogEnabled=true`
- `LogLevel=Info` hides `DEBUG`
- `LogLevel=Warn` shows only warnings and errors
- `LogLevel=Error` shows only errors

Important full-run behavior:

- any `-f=...` mode forces `LogToFile=false`

### `BaseURL`, `RawBaseURL`, and `WebhookURL`

These values are not decorative. They are used actively:

- `BaseURL`
  - builds app/package/depot/patchnote links
  - is also sent as the IRC realname field
- `RawBaseURL`
  - builds links to raw `app/*.vdf`, `sub/*.vdf`, and `ugc/*.json` artifacts
- `WebhookURL`
  - receives JSON payloads for selected events when configured

## 7. Steam Guard and Environment Variables

Steam Guard input and token recovery are implemented in:

- `/C:/git/SteamDatabaseBackend/Steam/Connection.cs`
- `/C:/git/SteamDatabaseBackend/Steam/WebAuth.cs`

### Supported environment variables

| Variable | Purpose |
| --- | --- |
| `STEAM_GUARD_FILE` | Path to a one-time Steam Guard env-style file. |
| `STEAM_GUARD_CODE_FILE` | Alternate path to a one-time Steam Guard env-style file. |
| `STEAM_GUARD_CODE` | Generic one-time Steam Guard code. |
| `STEAM_EMAIL_CODE` | One-time email Steam Guard code. |
| `STEAM_TWO_FACTOR_CODE` | One-time mobile authenticator code. |

### Guard file behavior

On non-Windows platforms, if no explicit guard-file env variable is set, the backend also checks:

```text
/run/steamdatabasebackend/steam-guard.env
```

Expected file format:

```bash
STEAM_GUARD_CODE=ABCDE
STEAM_EMAIL_CODE=ABCDE
STEAM_TWO_FACTOR_CODE=ABCDE
```

Behavior:

- the file is read directly by the application
- after a code is consumed, the file is deleted
- rejected non-interactive codes are not automatically retried in the same process
- environment-variable codes are also treated as one-shot values and cleared after use
- console input is only used as a fallback when interactive stdin is available

### Stored tokens and recovery

The backend stores Steam login state in `LocalConfig`:

- `backend.loginkey`
- `backend.guarddata`
- `backend.sessiontoken`

Recovery behavior:

- invalid or expired stored refresh tokens are cleared
- reconnect attempts use exponential backoff and jitter
- guard-code-related retries use a slower backoff to avoid hammering Steam
- repeated shutdown or reconnect loops are intentionally damped

## 8. Runtime Files and Directories

Relative to the executable directory:

| Path | Purpose |
| --- | --- |
| `settings.json` | Runtime configuration file. |
| `logs/` | Daily log files when `LogToFile=true`. |
| `app/` | Saved raw app VDF dumps created by the `app` command. |
| `sub/` | Saved raw package VDF dumps created by the `sub` command. |
| `ugc/` | Saved published file JSON dumps created by the `pubfile` command. |
| `files/depots_mapping.json` | Optional mapping from depot IDs to local folders for file downloads. |
| `files/files.json` | Optional file-match definitions for per-depot file downloads. |
| `files/<mapped-folder>/...` | Download target for selected files from important depots. |
| `files/update.sh` | Optional global update hook triggered after successful depot updates. |
| `files/<mapped-folder>/update.sh` | Optional per-folder update hook after file downloads. |

Important note:

- if `files/depots_mapping.json` is missing, file downloading is effectively disabled
- if `files/files.json` is missing, depots can still be processed, but no individual files are downloaded

## 9. Built-In HTTP API

Implementation:

- `/C:/git/SteamDatabaseBackend/Managers/HttpServer.cs`

Binding:

- listens only on `http://localhost:<BuiltInHttpServerPort>/`
- disabled when `BuiltInHttpServerPort` is `0`

Common behavior:

- if the Steam client is disconnected, the server returns `502 Bad Gateway`
- request logging goes through the normal logger
- unhandled exceptions return `500` with the exception message serialized as JSON

### Routes

| Route | Method | Parameters | Response |
| --- | --- | --- | --- |
| `/GetApp` | `GET` | `appid=<uint>` | Raw app VDF with `ETag`, or `404` if missing. |
| `/GetPackage` | `GET` | `subid=<uint>` | Raw package VDF with `ETag`, or `404` if missing. |
| `/GetPlayers` | `GET` | `appid=<uint>` | JSON `{ Success, Result, NumPlayers }`. |
| `/ReloadTokens` | `POST` | JSON body: array of app IDs | Reloads cached PICS tokens and optionally requests fresh tokens for listed apps. |
| `/ReloadImportant` | `GET` | none | Reloads `ImportantApps` and `ImportantSubs` from the database. |
| `/FullRun` | `GET` | none | Triggers metadata refresh tasks for apps and packages in the background. |
| `/Debug` | `GET` | none | JSON snapshot of connection and queue state. |

### `/Debug` fields

Current implementation returns:

- `SteamID`
- `CurrentEndPoint`
- `LastSuccessfulLogin`
- `LastServerRefreshTime`
- `JobsCount`
- `TasksCount`
- `CurrentlyProcessingKeys`
- `DepotLocksKeys`
  These are currently emitted as `DepotID:BranchName`.

## 10. IRC and Steam Chat Commands

Routing is implemented in:

- `/C:/git/SteamDatabaseBackend/IRC/CommandHandler.cs`

Rules:

- IRC commands must start with `IRC.CommandPrefix`
- Steam friend-chat commands use the same prefix
- only commands with `IsSteamCommand=true` are available in Steam friend chat
- all commands are available in IRC when IRC is enabled
- `queue` exists only when `Settings.IsMillhaven=true`

### Command reference

| Trigger | Steam friend chat | Usage | Behavior |
| --- | --- | --- | --- |
| `help` | no | `.help` | Lists registered commands. |
| `app` | yes | `.app <appid or partial game name>` | Fetches app PICS data, saves a raw VDF dump in `app/`, replies with public and raw links. Can trigger free-license request in ops channel when not owned. |
| `sub` | yes | `.sub <subid>` | Fetches package PICS data, saves a raw VDF dump in `sub/`, replies with public and raw links. |
| `depot` | no | `.depot <depotid>` | Looks up the depot in the database and replies with its name and depot URL. |
| `players` | yes | `.players <appid or partial game name>` | Requests current player count from Steam and combines it with `OnlineStats` daily counters when available. |
| `steamid` | yes | `.steamid <steamid> [individual/group/gamegroup]` | Expands or resolves Steam IDs and vanity URLs, then optionally resolves persona/clan info. |
| `gid` | no | `.gid <globalid>` | Decodes a Steam `GlobalID`. |
| `pubfile` | yes | `.pubfile <pubfileid>` | Requests workshop published file details, saves a raw JSON file in `ugc/`, and replies with file URLs. |
| `ugc` | yes | `.ugc <ugcid>` | Requests basic UGC details from Steam Cloud. |
| `enum` | no | `.enum <enumname> [value or substring [deprecated]]` | Reflects SteamKit enums and can search enum values. |
| `servers` | yes | `.servers <filter>` | Runs a master-server query using Valve's server query filter syntax. |
| `bins` | no | `.bins <osx/win32/ubuntu12> [stable]` | Returns Steam client binary manifest URLs from Valve CDN. |
| `queue` | no | `.queue <app/sub> <id>` | Millhaven-only queue insertion into `StoreUpdateQueue`. |

Notes:

- search-driven commands such as `app` and `players` can fall back to SteamDB Algolia search and then local database search
- Steam friend chat commands are ignored if Steam is not logged on
- IRC relay traffic from the hardcoded Discord relay host is normalized before command parsing

## 11. Database Overview

The baseline schema is provided in:

- `/C:/git/SteamDatabaseBackend/_database.sql`

This file is the required starting point for a fresh installation.

### Required baseline tables

#### App and package state

| Table | Purpose | Key columns |
| --- | --- | --- |
| `Apps` | Current top-level app state. | `AppID`, `AppType`, `Name`, `StoreName`, `LastKnownName`, `LastUpdated`, `LastDepotUpdate` |
| `AppsInfo` | Current normalized app key/value data. | `AppID`, `Key`, `Value` |
| `AppsHistory` | Historical app changes. | `ID`, `ChangeID`, `AppID`, `Action`, `Key`, `OldValue`, `NewValue`, `Diff` |
| `Subs` | Current top-level package state. | `SubID`, `Name`, `StoreName`, `LastKnownName`, `LastUpdated` |
| `SubsInfo` | Current normalized package key/value data. | `SubID`, `Key`, `Value` |
| `SubsHistory` | Historical package changes. | `ID`, `ChangeID`, `SubID`, `Action`, `Key`, `OldValue`, `NewValue` |
| `SubsApps` | Package membership edges. | `SubID`, `AppID`, `Type` where `Type` is `app` or `depot` |

#### Changelists and builds

| Table | Purpose | Key columns |
| --- | --- | --- |
| `Changelists` | Known Steam changelist numbers. | `ChangeID`, `Date` |
| `ChangelistsApps` | App membership for changelists. | `ChangeID`, `AppID` |
| `ChangelistsSubs` | Package membership for changelists. | `ChangeID`, `SubID` |
| `Builds` | Build metadata tracked by build ID and branch name. | `BuildID`, `ChangeID`, `AppID`, `Public`, `BranchName` |

#### Depots

| Table | Purpose | Key columns |
| --- | --- | --- |
| `Depots` | Preferred-branch depot summary state used by compatibility paths and simple lookups. | `DepotID`, `Name`, `PreferredBranchName`, `BuildID`, `ManifestID`, `LastManifestID`, `ManifestDate`, `FilenamesEncrypted`, `SizeOriginal`, `SizeCompressed`, `LastUpdated` |
| `DepotBranches` | Canonical current state per depot branch. | `DepotID`, `BranchName`, `BuildID`, `ManifestID`, `LastManifestID`, `ManifestDate`, `FilenamesEncrypted`, `SizeOriginal`, `SizeCompressed`, `LastUpdated` |
| `DepotsFiles` | Current file tree snapshot for a depot manifest branch. | `DepotID`, `BranchName`, `File`, `Hash`, `Size`, `Flags` |
| `DepotsHistory` | File and manifest delta history, branch-aware for manifest and file actions. | `ID`, `ChangeID`, `ManifestID`, `DepotID`, `BranchName`, `Action`, `File`, `OldValue`, `NewValue` |
| `DepotsKeys` | Stored depot decryption keys. | `DepotID`, `Key`, `Date` |

#### Lookup and support tables

| Table | Purpose | Key columns |
| --- | --- | --- |
| `KeyNames` | Key registry for app metadata keys. | `ID`, `Type`, `Name`, `DisplayName` |
| `KeyNamesSubs` | Key registry for package metadata keys. | `ID`, `Type`, `Name`, `DisplayName` |
| `PICSTokens` | Stored app PICS access tokens. | `AppID`, `Token`, `Date` |
| `PICSTokensSubs` | Stored package PICS access tokens. | `SubID`, `Token`, `Date` |
| `ImportantApps` | App IDs used by `ImportantOnly` workflows and announcements. | `AppID` |
| `ImportantSubs` | Package IDs used by `ImportantOnly` workflows and announcements. | `SubID` |
| `RSS` | De-duplication and history for RSS items. | `ID`, `Link`, `Title`, `Date` |
| `LocalConfig` | Runtime key/value storage for backend state. | `ConfigKey`, `Value` |

### `LocalConfig` keys used by the backend

The following keys are currently used by code:

| Key | Written by | Meaning |
| --- | --- | --- |
| `backend.changenumber` | `Application`, `PICSChanges` | Last persisted changelist number for incremental polling resume. |
| `backend.lastrsspost` | `RSS` | Last RSS publication timestamp seen by the backend. |
| `backend.guarddata` | `Connection` | Guard data returned by Steam credential auth. |
| `backend.loginkey` | `Connection`, `WebAuth` | Refresh token used for Steam logon and web auth. |
| `backend.sessiontoken` | `Connection` | Session token callback payload from Steam. |
| `backend.freelicense.requests` | `FreeLicense` | JSON queue of pending free-license package/app requests. |
| `backend.beta.requests` | `FreeLicense` | JSON queue of pending playtest/beta access requests. |
| `depot.<DepotID>` | `FileDownloader` | JSON state cache for downloaded chunks/files per important depot. |

### Optional tables and extended SteamDB environment

Some parts of the code expect additional tables that are not created by `_database.sql`.

These belong to the broader SteamDB environment and are optional unless you need the corresponding features.

| Table | Used by | Required for |
| --- | --- | --- |
| `Store` | `Settings`, `AppProcessor` | Detecting `IsMillhaven`, store-specific cleanup. |
| `StoreSubs` | `SubProcessor` | Millhaven package cleanup. |
| `StoreUpdateQueue` | `StoreQueue`, `queue` command, changelist queueing | Store update queue integration. |
| `SteamKeys` | `KeyActivator` | Automatic Steam key activation workflow. |
| `Patchnotes` | `RSS` | Official patch note enrichment and storage. |
| `OnlineStats` | `players` command | Current/daily player counters from the surrounding SteamDB stack. |

### `IsMillhaven`

`Settings.Initialize()` sets `Settings.IsMillhaven=true` when a table named `Store` exists.

That flag enables additional behavior such as:

- registering `KeyActivator`
- enabling the `queue` IRC command
- writing into `StoreUpdateQueue`
- store-oriented cleanup in processors
- RSS patch-note enrichment flow

## 12. Database-Backed Operational Inputs

Not all operational control happens through `settings.json`.

The following tables are also user-controlled inputs:

- `ImportantApps`
  - drives important app announcements
  - drives `-f=ImportantOnly`
- `ImportantSubs`
  - drives important package announcements
  - drives `-f=ImportantOnly`
- `PICSTokens`
  - reused in `TokensOnly` and regular request flows
- `PICSTokensSubs`
  - same for packages
- `LocalConfig`
  - stores persistent runtime state

Operationally important consequences:

- deleting `backend.changenumber` changes where incremental polling resumes
- deleting `backend.loginkey` forces credential-based login flow on the next reconnect
- editing `ImportantApps` or `ImportantSubs` changes both announcements and maintenance scope

## 13. Full-Run and Depot Notes

### `OnlyOwnedDepots`

`OnlyOwnedDepots` affects whether the backend downloads depot manifests for depots not owned by the logged-in Steam account.

Practical consequence:

- with `OnlyOwnedDepots=true`, depot rows may still be created or updated from app/package metadata
- but manifest-derived fields such as `ManifestDate`, `SizeOriginal`, `SizeCompressed`, `FilenamesEncrypted`, and file history only populate after a real manifest download

### Depot file downloads

Depot processing and file downloading are separate concerns:

- `DepotProcessor` tracks manifests, sizes, files, and depot history
- `FileDownloader` only downloads selected files for depots explicitly mapped in `files/depots_mapping.json` and `files/files.json`

### Current depot storage model

The baseline schema now splits depot data into:

- `Depots` as a preferred-branch summary row per `DepotID`
- `DepotBranches` as the canonical per-branch depot state
- `DepotsFiles` keyed by `DepotID + BranchName + File`
- `DepotsHistory` carrying `BranchName` for manifest and file history

Practical consequence:

- depot analytics can distinguish `public`, `beta`, `previous`, and other branches for the same depot
- simpler compatibility paths can still read `Depots` without joining `DepotBranches`

For broader greenfield modeling beyond the current implementation, see:

- `/C:/git/SteamDatabaseBackend/docs/depot-data-model-research.md`

## 14. Shutdown and Service Behavior

Graceful shutdown is implemented through:

- `/C:/git/SteamDatabaseBackend/Bootstrapper.cs`
- `/C:/git/SteamDatabaseBackend/Application.cs`

Handled signals:

- `SIGINT`
- `SIGTERM`

Shutdown flow:

1. stop accepting new work
2. dispose the built-in HTTP listener first
3. disconnect Steam and dispose Steam-side handlers
4. stop IRC and RSS if they were enabled
5. cancel tracked background tasks
6. persist `backend.changenumber` in normal daemon mode

Repeated signal behavior:

- a second shutdown signal forces immediate process exit

## 15. Recommended Documentation Map

The repository now has these main docs:

- `/C:/git/SteamDatabaseBackend/README.md`
  - short introduction and operational entry points
- `/C:/git/SteamDatabaseBackend/docs/deployment-checklist.md`
  - step-by-step deployment for Ubuntu and Windows, including database import
- `/C:/git/SteamDatabaseBackend/docs/backend-reference.md`
  - this full technical reference
- `/C:/git/SteamDatabaseBackend/docs/depot-data-model-research.md`
  - forward-looking depot schema research

## 16. Practical Defaults

For a normal service deployment:

- run without `-f`
- keep `LogLevel=Info`
- keep `SteamKitDebugLogEnabled=false`
- set `LogToFile=false` under `systemd` and use journald
- keep `BuiltInHttpServerPort` disabled unless you need the localhost API
- use a real Steam account if you need owned depots, web auth, or beta access
- import `/C:/git/SteamDatabaseBackend/_database.sql` before first start

For maintenance runs:

- use `-f=ImportantOnly` for a targeted refresh of curated IDs
- use `-f=NormalUsingMetadata` when you want a cheaper change-detection pass first
- use `-f=WithForcedDepots` only when you specifically want aggressive depot reprocessing

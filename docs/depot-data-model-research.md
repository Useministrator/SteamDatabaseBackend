# Depot Data Model Research

Recorded: 2026-04-07

## Purpose

This note captures the current research for a new depot storage model.

Assumptions:
- this is a greenfield instance;
- we do not need compatibility with the legacy `Depots` layout;
- the target shape should be closer to how SteamDB presents depot, manifest, and branch data.

## Why A New Model Is Needed

The legacy repository stores one current row per `DepotID`, while branch-specific data already exists conceptually in the runtime flow. That is not sufficient if we want to store:
- separate `public`, `beta`, and private branch states;
- build metadata per branch;
- full manifest history per depot;
- file trees per manifest without branch collisions;
- app-specific depot relationships similar to SteamDB.

For a clean redesign, the canonical "current state" key should not be just `DepotID`. It should include the app branch context.

## Observed SteamDB Properties

SteamDB surfaces several independent layers of data around depots:

### App depots page

Observed on pages such as:
- [App 365900 depots](https://steamdb.info/app/365900/depots/)
- [App 1304640 depots](https://steamdb.info/app/1304640/depots/)
- [App 2767950 depots](https://steamdb.info/app/2767950/depots/)
- [App 6700 depots](https://steamdb.info/app/6700/depots/)

Visible properties include:
- branch name;
- branch description;
- `Build ID`;
- `Time Built`;
- `Time Updated`;
- depot `Configuration`;
- size columns such as `Size` and `DL`;
- groupings like `Depots`, `Redistributables`, and DLC-related depot sections;
- app-level flags such as `privatebranches`, `baselanguages`, `hasdepotsindlc`, `preloadonly`, and `overridescddb`.

### Depot page

Observed on pages such as:
- [Depot 1124212](https://steamdb.info/depot/1124212/)
- [Depot 1740492](https://steamdb.info/depot/1740492/)
- [Depot 4401861](https://steamdb.info/depot/4401861/)

Visible properties include:
- `Last known name`;
- manifest-centric metadata;
- compressed and uncompressed sizes;
- file type summary;
- package references;
- app references;
- encrypted filename scenarios;
- cases where a depot exists but some file-level data is unavailable.

### Depot manifests and history

Observed on pages such as:
- [Depot 3374502 manifests](https://steamdb.info/depot/3374502/manifests/)
- [Depot 1712681 manifests](https://steamdb.info/depot/1712681/manifests/)
- [Depot 3398970 history](https://steamdb.info/depot/3398970/history/)
- [Depot 440 history](https://steamdb.info/depot/440/history/)

Visible properties include:
- manifest identifiers;
- manifest dates;
- previously seen manifests;
- branch filtering;
- file-level change history;
- "last public update" style summaries derived from branch observations.

## Modeling Principles

1. Depot identity is not the same thing as branch state.
2. Branch state is not the same thing as manifest identity.
3. File trees belong to a manifest, not to a branch row.
4. App branch context matters.
5. Summary views should be derived, not canonical.

The most important practical rule is this:

`(AppID, BranchName, DepotID)` should be the canonical key for current branch state.

Reason:
- `Build ID`, branch description, and update timestamps belong to the app branch;
- the same depot can be reused by more than one app or app branch;
- the same manifest can appear in more than one branch over time.

## Recommended Canonical Tables

### `depots`

Depot identity and long-lived metadata.

Suggested columns:
- `DepotID`
- `LastKnownName`
- `FirstSeenAt`
- `LastSeenAt`
- `HasEncryptedFilenames`
- `HasDecryptionKey`
- `HasFileHashes`
- `HashVisibility`

Primary key:
- `DepotID`

### `app_branches`

Branch metadata at the app level.

Suggested columns:
- `AppID`
- `BranchName`
- `Description`
- `BuildID`
- `TimeBuilt`
- `TimeUpdated`
- `Access`
- `IsDefaultBranch`
- `LastSeenAt`

Primary key:
- `(AppID, BranchName)`

### `app_depots`

Relationship between an app and a depot.

Suggested columns:
- `AppID`
- `DepotID`
- `RelationKind`
- `ConfigurationLabel`
- `DlcAppID`
- `SourceAppID`
- `SourceDepotID`
- `OsList`
- `LanguagesJson`
- `Architecture`
- `LastSeenAt`

Primary key:
- `(AppID, DepotID)`

### `branch_depot_state`

Current manifest state of a depot within an app branch.

Suggested columns:
- `AppID`
- `BranchName`
- `DepotID`
- `ManifestID`
- `PreviousManifestID`
- `ManifestDate`
- `SizeOriginal`
- `SizeCompressed`
- `FilesCount`
- `FilenamesEncrypted`
- `HasDecryptionKey`
- `LastObservedAt`

Primary key:
- `(AppID, BranchName, DepotID)`

Important note:
- this is the canonical "what does this branch point to right now?" table.

### `depot_manifests`

Immutable manifest-level data.

Suggested columns:
- `DepotID`
- `ManifestID`
- `CreatedAt`
- `SizeOriginal`
- `SizeCompressed`
- `FilesCount`
- `FilenamesEncrypted`
- `HasDecryptionKey`
- `FirstSeenAt`
- `LastSeenAt`

Primary key:
- `(DepotID, ManifestID)`

### `depot_manifest_occurrences`

A record that a specific manifest was observed on a specific app branch.

Suggested columns:
- `DepotID`
- `ManifestID`
- `AppID`
- `BranchName`
- `BuildID`
- `SeenAt`

Primary key:
- `(DepotID, ManifestID, AppID, BranchName)`

This table is what enables:
- branch filters on manifest pages;
- "previously seen manifests";
- per-branch manifest timelines.

### `depot_manifest_files`

Canonical file tree for a concrete manifest.

Suggested columns:
- `DepotID`
- `ManifestID`
- `Path`
- `Hash`
- `Size`
- `Flags`

Primary key:
- `(DepotID, ManifestID, Path)`

Important note:
- files should be keyed by manifest, not by branch;
- this avoids duplicating identical file trees when multiple branches point to the same manifest.

### `depot_manifest_filetypes`

Precomputed file-type summary for a manifest.

Suggested columns:
- `DepotID`
- `ManifestID`
- `FileType`
- `FilesCount`
- `TotalSize`

Primary key:
- `(DepotID, ManifestID, FileType)`

### `depot_packages`

Observed package references to a depot.

Suggested columns:
- `DepotID`
- `SubID`
- `RelationKind`
- `FirstSeenAt`
- `LastSeenAt`

Primary key:
- `(DepotID, SubID)`

### `depot_apps`

Observed app references to a depot.

Suggested columns:
- `DepotID`
- `AppID`
- `RelationKind`
- `FirstSeenAt`
- `LastSeenAt`

Primary key:
- `(DepotID, AppID)`

## Recommended Derived Projections

These should be materialized views or summary tables, not sources of truth:
- "current depot view" for UI;
- "last public update";
- current preferred branch per depot;
- per-app totals filtered by OS, language, or depot type;
- aggregate counters such as files, manifests, packages, and apps;
- download-size savings percentages.

## Explicit Design Decisions

### Keep branch state separate from manifest state

Do not store file trees directly under branch rows.

Good:
- branch points to manifest;
- manifest owns file tree.

Bad:
- branch row directly owns files;
- duplicate file trees when the same manifest appears on multiple branches.

### Use app branch context in the current-state key

Do not key current branch state as only `(DepotID, BranchName)`.

Reason:
- the same depot can be shared across apps;
- branch metadata such as `BuildID` belongs to the app branch, not to the depot globally.

### Treat summaries as projections

If a UI wants one "preferred" row for a depot, compute it from canonical tables.

Do not make that preferred row the source of truth.

## Good Future Extensions

Likely useful later:
- branch passwords or branch access policy metadata if it becomes observable;
- crawler ownership coverage such as "owned by bot account";
- manifest download health and retry diagnostics;
- branch-level visibility flags for UI;
- per-manifest chunk statistics if file download analytics are needed.

## Open Questions

These do not block schema work, but should be decided during implementation:
- exact enum values for `RelationKind`;
- whether `LanguagesJson` should stay JSON or be normalized into a separate table;
- whether `OsList` should be normalized;
- whether file hashes should support "unavailable" versus "hidden" as distinct states;
- whether current-state summaries should be stored physically or served from SQL views.

## Implementation Direction

If this design is adopted, runtime processing should work like this:
- parse app branches first;
- parse app-to-depot relationships second;
- create one current-state job per `(AppID, BranchName, DepotID)`;
- download or refresh manifest data keyed by `(DepotID, ManifestID)`;
- store file trees keyed by `(DepotID, ManifestID)`;
- record manifest appearances in `depot_manifest_occurrences`;
- build UI summaries from projections.

## Sources

- [SteamDB app depots example: 365900](https://steamdb.info/app/365900/depots/)
- [SteamDB app depots example: 1304640](https://steamdb.info/app/1304640/depots/)
- [SteamDB app depots example: 2767950](https://steamdb.info/app/2767950/depots/)
- [SteamDB app depots example: 6700](https://steamdb.info/app/6700/depots/)
- [SteamDB depot example: 1124212](https://steamdb.info/depot/1124212/)
- [SteamDB depot example: 1740492](https://steamdb.info/depot/1740492/)
- [SteamDB depot example: 4401861](https://steamdb.info/depot/4401861/)
- [SteamDB manifests example: 3374502](https://steamdb.info/depot/3374502/manifests/)
- [SteamDB manifests example: 1712681](https://steamdb.info/depot/1712681/manifests/)
- [SteamDB history example: 3398970](https://steamdb.info/depot/3398970/history/)
- [SteamDB history example: 440](https://steamdb.info/depot/440/history/)

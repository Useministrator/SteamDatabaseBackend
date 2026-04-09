/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using SteamKit2;
using SteamKit2.CDN;

namespace SteamDatabaseBackend
{
    internal class DepotProcessor : IDisposable
    {
        private const string DefaultServer = "origin1-sea1.steamcontent.com";

        public class ManifestJob
        {
            public uint AppId;
            public uint ChangeNumber;
            public uint DepotId;
            public int BuildId;
            public string BranchName = "public";
            public bool IsPreferredBranch;
            public ulong ManifestId;
            public ulong LastManifestId;
            public string DepotName;
            public Server Server;
            public byte[] DepotKey;
            public EResult Result = EResult.Fail;
            public bool StoredFilenamesEncrypted;
            public bool DownloadCorrupted;
        }

        public const string HistoryQuery = "INSERT INTO `DepotsHistory` (`ManifestID`, `ChangeID`, `DepotID`, `BranchName`, `File`, `Action`, `OldValue`, `NewValue`) VALUES (@ManifestID, @ChangeID, @DepotID, @BranchName, @File, @Action, @OldValue, @NewValue)";

        private static readonly object UpdateScriptLock = new object();
        private static readonly TimeSpan CdnAuthTokenRefreshWindow = TimeSpan.FromMinutes(1);

        private sealed class CachedCdnAuthToken
        {
            public string Token { get; init; }
            public DateTime Expiration { get; init; }
        }

        private readonly Dictionary<string, byte> DepotLocks = new Dictionary<string, byte>();
        private readonly ConcurrentDictionary<(uint AppId, uint DepotId, string Host), CachedCdnAuthToken> CdnAuthTokens = new();
        private readonly ConcurrentDictionary<(uint AppId, uint DepotId, string Host), SemaphoreSlim> CdnAuthTokenLocks = new();
        private SemaphoreSlim ManifestDownloadSemaphore = new SemaphoreSlim(15);
        private readonly string UpdateScript;

        private Client CDNClient;
        private List<Server> CDNServers;

        public int DepotLocksCount => DepotLocks.Count;
        public Dictionary<string, byte>.KeyCollection DepotLocksKeys => DepotLocks.Keys;
        public DateTime LastServerRefreshTime { get; private set; } = DateTime.Now;

        private Regex DepotNameStart;
        private Regex DepotNameEnd;

        public DepotProcessor(SteamClient client)
        {
            UpdateScript = Path.Combine(Application.Path, "files", "update.sh");
            CDNClient = new Client(client);
            CDNServers = new List<Server>
            {
                new DnsEndPoint(DefaultServer, 80)
            };

            Client.RequestTimeout = TimeSpan.FromSeconds(30);

            FileDownloader.SetCDNClient(CDNClient, GetCdnAuthTokenAsync, InvalidateCdnAuthToken);

            DepotNameStart = new Regex("^(\u00dalo\u017ei\u0161t\u011b \u2013|\u0425\u0440\u0430\u043d\u0438\u043b\u0438\u0449\u0435|D\u00e9p\u00f4t :|Depot|Depot:|Repositorio) ", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);
            DepotNameEnd = new Regex(" (- magazyn zawarto\u015bci|\u03bd\u03c4\u03b5\u03c0\u03cc|\u0441\u0445\u043e\u0432\u0438\u0449\u0435|\u0e14\u0e35\u0e42\u0e1b|\u2013 depot|\u30c7\u30dd|\u4e2a Depot|\uac1c\uc758 \ub514\ud3ec|dep\u00e5|dep\u00f3|Depo|Depot|depot|depotti)$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);
        }

        private static string GetDisplayAppName(string appName, uint appId)
        {
            appName = Utils.RemoveControlCharacters(appName ?? string.Empty).Trim();

            return string.IsNullOrEmpty(appName) ? $"AppID {appId}" : appName;
        }

        private static string FormatDepotHint(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Trim().ToLowerInvariant() switch
            {
                "windows" => "Windows",
                "linux" => "Linux",
                "macos" => "macOS",
                "mac" => "macOS",
                "64" => "64-bit",
                "64bit" => "64-bit",
                "x64" => "64-bit",
                "32" => "32-bit",
                "32bit" => "32-bit",
                "x86" => "32-bit",
                _ => value.Trim(),
            };
        }

        private static string GetDepotName(KeyValue depot, uint appId, uint depotId, string explicitDepotName)
        {
            explicitDepotName = Utils.RemoveControlCharacters(explicitDepotName ?? string.Empty).Trim();

            if (!string.IsNullOrEmpty(explicitDepotName))
            {
                return explicitDepotName;
            }

            var appName = GetDisplayAppName(Steam.GetAppName(appId), appId);
            var hints = new List<string>();
            var config = depot["config"];

            if (config != KeyValue.Invalid)
            {
                var osList = config["oslist"].AsString();

                if (!string.IsNullOrWhiteSpace(osList))
                {
                    var formattedOs = string.Join("/", osList
                        .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(FormatDepotHint)
                        .Where(x => !string.IsNullOrEmpty(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase));

                    if (!string.IsNullOrEmpty(formattedOs))
                    {
                        hints.Add(formattedOs);
                    }
                }

                var osArch = FormatDepotHint(config["osarch"].AsString());

                if (!string.IsNullOrEmpty(osArch))
                {
                    hints.Add(osArch);
                }

                var language = config["language"].AsString();

                if (!string.IsNullOrWhiteSpace(language))
                {
                    hints.Add(language.Trim());
                }

                if (config["lowviolence"].AsBoolean())
                {
                    hints.Add("low violence");
                }
            }

            if (depot["sharedinstall"].AsBoolean())
            {
                hints.Add("shared");
            }

            if (uint.TryParse(depot["dlcappid"].Value, out var dlcAppId))
            {
                hints.Add($"DLC {dlcAppId}");
            }

            return hints.Count == 0
                ? $"{appName} Depot {depotId}"
                : $"{appName} Depot {depotId} ({string.Join(", ", hints.Distinct(StringComparer.OrdinalIgnoreCase))})";
        }

        private static string GetDepotLockKey(uint depotId, string branchName)
        {
            return $"{depotId}:{branchName}";
        }

        public void Dispose()
        {
            if (CDNClient != null)
            {
                CDNClient.Dispose();
                CDNClient = null;
            }

            if (ManifestDownloadSemaphore != null)
            {
                ManifestDownloadSemaphore.Dispose();
                ManifestDownloadSemaphore = null;
            }
        }

        public async Task UpdateContentServerList()
        {
            LastServerRefreshTime = DateTime.Now;

            KeyValue response;

            using (var steamDirectory = Steam.Configuration.GetAsyncWebAPIInterface("IContentServerDirectoryService"))
            {
                steamDirectory.Timeout = TimeSpan.FromSeconds(30);

                try
                {
                    response = await steamDirectory.CallAsync(HttpMethod.Get, "GetServersForSteamPipe", 1,
                        new Dictionary<string, object>
                        {
                            { "cell_id", Steam.Instance.Client.CellID },
                            { "max_servers", "100" }
                        });

                    if (response["servers"] == KeyValue.Invalid)
                    {
                        throw new Exception("response.servers is invalid");
                    }
                }
                catch (Exception e)
                {
                    Log.WriteError(nameof(DepotProcessor), $"Failed to get server list: {e.Message}");

                    return;
                }
            }

            var newServers = new List<Server>();

            foreach (var server in response["servers"].Children)
            {
                if (server["allowed_app_ids"] != KeyValue.Invalid)
                {
                    continue;
                }

                if (server["type"].AsString() != "SteamCache" && server["type"].AsString() != "CDN")
                {
                    continue;
                }

                newServers.Add(new DnsEndPoint(server["host"].AsString(), server["https_support"].AsString() == "mandatory" ? 443 : 80));
            }

            if (newServers.Count > 0)
            {
                CDNServers = newServers;
            }

            Log.WriteInfo(nameof(DepotProcessor), $"Received {newServers.Count} download servers");
        }

        public async Task Process(IDbConnection db, uint appID, uint changeNumber, KeyValue depots)
        {
            var requests = new List<ManifestJob>();
            var dlcNames = new Dictionary<uint, string>();

            // Get data in format we want first
            foreach (var depot in depots.Children)
            {
                // Ignore keys that aren't integers, for example "branches"
                if (!uint.TryParse(depot.Name, out var depotId))
                {
                    continue;
                }

                // Ignore these for now, parent app should be updated too anyway
                if (depot["depotfromapp"].Value != null)
                {
                    continue;
                }

                var explicitDepotName = depot["name"].AsString();
                var depotName = GetDepotName(depot, appID, depotId, explicitDepotName);

                if (!string.IsNullOrEmpty(explicitDepotName) && depot["dlcappid"].Value != null)
                {
                    if (uint.TryParse(depot["dlcappid"].Value, out var dlcAppId))
                    {
                        dlcNames[dlcAppId] = explicitDepotName;
                    }
                }

                var validManifestBranches = depot["manifests"].Children
                    .Where(branch => branch.Name != "local" && TryGetManifestId(branch, out _))
                    .ToList();

                if (validManifestBranches.Count == 0)
                {
                    await db.ExecuteAsync(
                        "INSERT INTO `Depots` (`DepotID`, `Name`) VALUES (@DepotID, @DepotName) ON DUPLICATE KEY UPDATE `Name` = VALUES(`Name`)",
                        new
                        {
                            DepotID = depotId,
                            DepotName = depotName,
                        });

                    continue;
                }

                var preferredBranchName = validManifestBranches
                    .FirstOrDefault(branch => branch.Name.Equals("public", StringComparison.OrdinalIgnoreCase))
                    ?.Name ?? validManifestBranches[0].Name;

                foreach (var manifestBranch in validManifestBranches)
                {
                    var branchName = string.IsNullOrEmpty(manifestBranch.Name) ? "public" : manifestBranch.Name;
                    var lockKey = GetDepotLockKey(depotId, branchName);

                    lock (DepotLocks)
                    {
                        if (DepotLocks.ContainsKey(lockKey))
                        {
                            continue;
                        }
                    }

                    requests.Add(new ManifestJob
                    {
                        AppId = appID,
                        ChangeNumber = changeNumber,
                        DepotId = depotId,
                        BuildId = depots["branches"][branchName]["buildid"].AsInteger(),
                        BranchName = branchName,
                        IsPreferredBranch = branchName.Equals(preferredBranchName, StringComparison.OrdinalIgnoreCase),
                        ManifestId = ParseManifestId(manifestBranch),
                        DepotName = depotName,
                    });
                }
            }

            if (dlcNames.Any())
            {
                await ProcessDlcNames(appID, changeNumber, dlcNames);
            }

            foreach (var branch in depots["branches"].Children)
            {
                var buildId = branch["buildid"].AsInteger();

                if (buildId < 1)
                {
                    continue;
                }

                var isPublic = branch.Name != null && branch.Name.Equals("public", StringComparison.OrdinalIgnoreCase);

                await db.ExecuteAsync(isPublic ?
                        "INSERT INTO `Builds` (`BuildID`, `ChangeID`, `AppID`, `Public`, `BranchName`) VALUES (@BuildID, @ChangeNumber, @AppID, 1, @BranchName) ON DUPLICATE KEY UPDATE `ChangeID` = IF(`Public` = 0, VALUES(`ChangeID`), `ChangeID`), `Public` = 1, `BranchName` = \"public\""
                        :
                        "INSERT INTO `Builds` (`BuildID`, `ChangeID`, `AppID`, `Public`, `BranchName`) VALUES (@BuildID, @ChangeNumber, @AppID, 0, @BranchName) ON DUPLICATE KEY UPDATE `AppID` = `AppID`",
                    new
                    {
                        BuildID = buildId,
                        ChangeNumber = changeNumber,
                        AppID = appID,
                        BranchName = isPublic ? "public" : branch.Name,
                    });
            }

            if (requests.Count == 0)
            {
                return;
            }

            var depotsToDownload = new List<ManifestJob>();

            var depotIds = requests.Select(x => x.DepotId).ToList();
            var dbDepots = (await db.QueryAsync<Depot>("SELECT `DepotID`, `Name`, `PreferredBranchName`, `BuildID`, `ManifestID`, `LastManifestID`, `FilenamesEncrypted` FROM `Depots` WHERE `DepotID` IN @depotIds", new { depotIds }))
                .ToDictionary(x => x.DepotID, x => x);
            var dbDepotBranches = (await db.QueryAsync<DepotBranch>("SELECT `DepotID`, `BranchName`, `BuildID`, `ManifestID`, `LastManifestID`, `FilenamesEncrypted` FROM `DepotBranches` WHERE `DepotID` IN @depotIds", new { depotIds }))
                .ToDictionary(x => (x.DepotID, x.BranchName), x => x);

            var decryptionKeys = (await db.QueryAsync<DepotKey>("SELECT `DepotID`, `Key` FROM `DepotsKeys` WHERE `DepotID` IN @depotIds", new { depotIds }))
                .ToDictionary(x => x.DepotID, x => Utils.StringToByteArray(x.Key));

            foreach (var request in requests)
            {
                dbDepots.TryGetValue(request.DepotId, out var dbDepot);
                dbDepot ??= new Depot();

                dbDepotBranches.TryGetValue((request.DepotId, request.BranchName), out var dbDepotBranch);
                dbDepotBranch ??= new DepotBranch
                {
                    DepotID = request.DepotId,
                    BranchName = request.BranchName,
                };

                decryptionKeys.TryGetValue(request.DepotId, out request.DepotKey);

                if (dbDepotBranch.BuildID > request.BuildId)
                {
                    Log.WriteDebug(nameof(DepotProcessor), $"Skipping depot {request.DepotId} branch {request.BranchName} due to old buildid: {dbDepotBranch.BuildID} > {request.BuildId}");

                    continue;
                }

                request.StoredFilenamesEncrypted = dbDepotBranch.FilenamesEncrypted;
                request.LastManifestId = dbDepotBranch.LastManifestID;

                if (request.IsPreferredBranch && (dbDepot.BuildID != request.BuildId
                    || dbDepot.ManifestID != request.ManifestId
                    || request.DepotName != dbDepot.Name
                    || !string.Equals(dbDepot.PreferredBranchName, request.BranchName, StringComparison.OrdinalIgnoreCase)))
                {
                    await db.ExecuteAsync(
                        @"INSERT INTO `Depots` (`DepotID`, `Name`, `PreferredBranchName`, `BuildID`, `ManifestID`) VALUES (@DepotID, @DepotName, @PreferredBranchName, @BuildID, @ManifestID)
                          ON DUPLICATE KEY UPDATE `LastUpdated` = CURRENT_TIMESTAMP(), `Name` = VALUES(`Name`), `PreferredBranchName` = VALUES(`PreferredBranchName`), `BuildID` = VALUES(`BuildID`), `ManifestID` = VALUES(`ManifestID`)",
                        new
                        {
                            DepotID = request.DepotId,
                            DepotName = request.DepotName,
                            PreferredBranchName = request.BranchName,
                            BuildID = request.BuildId,
                            ManifestID = request.ManifestId,
                        });
                }

                if (dbDepotBranch.BuildID != request.BuildId || dbDepotBranch.ManifestID != request.ManifestId)
                {
                    await db.ExecuteAsync(
                        @"INSERT INTO `DepotBranches` (`DepotID`, `BranchName`, `BuildID`, `ManifestID`) VALUES (@DepotID, @BranchName, @BuildID, @ManifestID)
                          ON DUPLICATE KEY UPDATE `LastUpdated` = CURRENT_TIMESTAMP(), `BuildID` = VALUES(`BuildID`), `ManifestID` = VALUES(`ManifestID`)",
                        new
                        {
                            DepotID = request.DepotId,
                            request.BranchName,
                            BuildID = request.BuildId,
                            ManifestID = request.ManifestId,
                        });
                }

                if (dbDepotBranch.ManifestID != request.ManifestId)
                {
                    await MakeHistory(db, null, request, string.Empty, "manifest_change", dbDepotBranch.ManifestID, request.ManifestId);
                }

                if (dbDepotBranch.LastManifestID == request.ManifestId
                    && dbDepotBranch.ManifestID == request.ManifestId
                    && Settings.FullRun != FullRunState.WithForcedDepots
                    && !dbDepotBranch.FilenamesEncrypted
                    && request.DepotKey != null)
                {
                    continue;
                }

                if (Settings.Current.OnlyOwnedDepots && !LicenseList.OwnedDepots.ContainsKey(request.DepotId))
                {
                    continue;
                }

                lock (DepotLocks)
                {
                    DepotLocks.Add(GetDepotLockKey(request.DepotId, request.BranchName), 1);
                }

                depotsToDownload.Add(request);
            }

            if (depotsToDownload.Count > 0)
            {
                _ = TaskManager.Run(async () => await DownloadDepots(appID, depotsToDownload)).ContinueWith(task =>
                {
                    Log.WriteError(nameof(DepotProcessor), $"An exception occured when processing depots from app {appID}, removing locks");

                    foreach (var depot in depotsToDownload)
                    {
                        RemoveLock(depot.DepotId, depot.BranchName);
                    }
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        private static async Task GetDepotDecryptionKey(SteamApps instance, ManifestJob depot, uint appID)
        {
            var task = instance.GetDepotDecryptionKey(depot.DepotId, appID);
            task.Timeout = TimeSpan.FromMinutes(15);

            SteamApps.DepotKeyCallback callback;

            try
            {
                callback = await task;
            }
            catch (TaskCanceledException)
            {
                Log.WriteWarn(nameof(DepotProcessor), $"Decryption key timed out for {depot.DepotId}");

                return;
            }

            if (callback.Result != EResult.OK)
            {
                if (callback.Result != EResult.AccessDenied)
                {
                    Log.WriteWarn(nameof(DepotProcessor), $"No access to depot {depot.DepotId} ({callback.Result})");
                }

                return;
            }

            Log.WriteDebug(nameof(DepotProcessor), $"Got a new depot key for depot {depot.DepotId}");

            await using (var db = await Database.GetConnectionAsync())
            {
                await db.ExecuteAsync("INSERT INTO `DepotsKeys` (`DepotID`, `Key`) VALUES (@DepotID, @Key) ON DUPLICATE KEY UPDATE `Key` = VALUES(`Key`)", new { DepotID = depot.DepotId, Key = Utils.ByteArrayToString(callback.DepotKey) });
            }

            depot.DepotKey = callback.DepotKey;
        }
        public SteamContent steamContent;
        public async Task<ulong> GetDepotManifestRequestCodeAsync(uint depotId, uint appId, ulong manifestId, string branch)
        {
            steamContent = Steam.Instance.Client.GetHandler<SteamContent>();
            var requestCode = await steamContent.GetManifestRequestCode(depotId, appId, manifestId, branch);

            if (requestCode == 0)
            {
                Log.WriteDebug(nameof(DepotProcessor), $"No manifest request code granted for app {appId} depot {depotId} manifest {manifestId} ({branch})");
            }
            else
            {
                Log.WriteDebug(nameof(DepotProcessor), $"Got manifest request code for {depotId} {manifestId} result: {requestCode}");
            }

            return requestCode;
        }

        private async Task<string> GetCdnAuthTokenAsync(ManifestJob depot, Server server, bool forceRefresh = false)
        {
            if (server == null)
            {
                return null;
            }

            var cacheKey = (depot.AppId, depot.DepotId, server.Host);

            if (!forceRefresh
                && CdnAuthTokens.TryGetValue(cacheKey, out var cachedToken)
                && cachedToken.Expiration > DateTime.UtcNow.Add(CdnAuthTokenRefreshWindow))
            {
                return cachedToken.Token;
            }

            var authTokenLock = CdnAuthTokenLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));

            await authTokenLock.WaitAsync(TaskManager.TaskCancellationToken.Token).ConfigureAwait(false);

            try
            {
                if (!forceRefresh
                    && CdnAuthTokens.TryGetValue(cacheKey, out cachedToken)
                    && cachedToken.Expiration > DateTime.UtcNow.Add(CdnAuthTokenRefreshWindow))
                {
                    return cachedToken.Token;
                }

                steamContent ??= Steam.Instance.Client.GetHandler<SteamContent>();

                var cdnAuthToken = await steamContent.GetCDNAuthToken(depot.AppId, depot.DepotId, server.Host).ConfigureAwait(false);

                if (cdnAuthToken.Result != EResult.OK || string.IsNullOrEmpty(cdnAuthToken.Token))
                {
                    CdnAuthTokens.TryRemove(cacheKey, out _);

                    Log.WriteDebug(nameof(DepotProcessor), $"Failed to get CDN auth token for app {depot.AppId} depot {depot.DepotId} host {server.Host}: {cdnAuthToken.Result}");

                    return null;
                }

                var expiration = cdnAuthToken.Expiration == default
                    ? DateTime.UtcNow.AddMinutes(5)
                    : cdnAuthToken.Expiration.ToUniversalTime();

                CdnAuthTokens[cacheKey] = new CachedCdnAuthToken
                {
                    Token = cdnAuthToken.Token,
                    Expiration = expiration,
                };

                Log.WriteDebug(nameof(DepotProcessor), $"Got CDN auth token for app {depot.AppId} depot {depot.DepotId} host {server.Host} (expires {cdnAuthToken.Expiration:u})");

                return cdnAuthToken.Token;
            }
            finally
            {
                authTokenLock.Release();
            }
        }

        private void InvalidateCdnAuthToken(ManifestJob depot, Server server)
        {
            if (server == null)
            {
                return;
            }

            CdnAuthTokens.TryRemove((depot.AppId, depot.DepotId, server.Host), out _);
        }

        private static bool TryGetManifestId(KeyValue branch, out ulong manifestId)
        {
            manifestId = 0;

            if (branch == KeyValue.Invalid)
            {
                return false;
            }

            if (branch.Value != null && ulong.TryParse(branch.Value, out manifestId))
            {
                return true;
            }

            var gid = branch["gid"];

            return gid.Value != null && ulong.TryParse(gid.Value, out manifestId);
        }

        private static ulong ParseManifestId(KeyValue branch)
        {
            if (TryGetManifestId(branch, out var manifestId))
            {
                return manifestId;
            }

            throw new InvalidDataException($"Failed to parse manifest id from branch {branch.Name}");
        }

        private async Task DownloadDepots(uint appID, List<ManifestJob> depots)
        {
            Log.WriteDebug(nameof(DepotProcessor), $"Will process {depots.Count} depots from app {appID} ({DepotLocks.Count} depot locks left)");

            var processTasks = new List<Task<(uint DepotID, string BranchName, EResult Result)>>();
            var anyFilesDownloaded = false;
            var willDownloadFiles = false;

            foreach (var depot in depots)
            {
                if (depot.DepotKey == null
                    && depot.LastManifestId == depot.ManifestId
                    && Settings.FullRun != FullRunState.WithForcedDepots)
                {
                    RemoveLock(depot.DepotId, depot.BranchName);

                    continue;
                }

                depot.Server = GetContentServer();

                DepotManifest depotManifest = null;
                var lastError = string.Empty;

                var manifestRequestCode = await GetDepotManifestRequestCodeAsync(depot.DepotId, appID, depot.ManifestId, depot.BranchName);
                if (manifestRequestCode == 0)
                {
                    RemoveLock(depot.DepotId, depot.BranchName);

                    continue;
                }

                var refreshCdnAuthToken = false;

                for (var i = 0; i <= 5; i++)
                {
                    try
                    {
                        await ManifestDownloadSemaphore.WaitAsync(TaskManager.TaskCancellationToken.Token).ConfigureAwait(false);
                        var cdnAuthToken = await GetCdnAuthTokenAsync(depot, depot.Server, refreshCdnAuthToken).ConfigureAwait(false);
                        refreshCdnAuthToken = false;
                        depotManifest = await CDNClient.DownloadManifestAsync(depot.DepotId, depot.ManifestId, manifestRequestCode, depot.Server, depot.DepotKey, null, cdnAuthToken);

                        break;
                    }
                    catch (SteamKitWebRequestException e) when (e.StatusCode == HttpStatusCode.Unauthorized || e.StatusCode == HttpStatusCode.Forbidden)
                    {
                        refreshCdnAuthToken = true;
                        InvalidateCdnAuthToken(depot, depot.Server);
                        lastError = e.Message;

                        Log.WriteError(nameof(DepotProcessor), $"Failed to download depot manifest for app {appID} depot {depot.DepotId} ({depot.Server}: {lastError}) (#{i})");
                    }
                    catch (Exception e)
                    {
                        lastError = e.Message;

                        Log.WriteError(nameof(DepotProcessor), $"Failed to download depot manifest for app {appID} depot {depot.DepotId} ({depot.Server}: {lastError}) (#{i})");
                    }
                    finally
                    {
                        ManifestDownloadSemaphore.Release();
                    }

                    if (depot.DepotKey != null)
                    {
                        RemoveErroredServer(depot.Server);
                    }

                    if (depotManifest == null && i < 5)
                    {
                        await Task.Delay(Utils.ExponentionalBackoff(i + 1));

                        depot.Server = GetContentServer();
                    }
                }

                if (depotManifest == null)
                {
                    RemoveLock(depot.DepotId, depot.BranchName);

                    if (FileDownloader.IsImportantDepot(depot.DepotId))
                    {
                        IRC.Instance.SendOps($"{Colors.OLIVE}[{depot.DepotName} / {depot.BranchName}]{Colors.NORMAL} Failed to download manifest ({lastError})");
                    }

                    if (!Settings.IsFullRun && depot.DepotKey != null)
                    {
                        JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(appID, null));
                    }

                    continue;
                }

                if (depot.DepotKey == null && (depotManifest.FilenamesEncrypted || FileDownloader.IsImportantDepot(depot.DepotId)))
                {
                    await GetDepotDecryptionKey(Steam.Instance.Apps, depot, appID);
                }

                var task = ProcessDepotAfterDownload(depot, depotManifest);

                processTasks.Add(task);

                if (!FileDownloader.IsImportantDepot(depot.DepotId)
                    || depot.DepotKey == null
                    || !depot.BranchName.Equals("public", StringComparison.OrdinalIgnoreCase))
                {
                    depot.Result = EResult.Ignored;
                    continue;
                }

                willDownloadFiles = true;

                task = TaskManager.Run(async () =>
                {
                    var result = EResult.Fail;

                    try
                    {
                        result = await FileDownloader.DownloadFilesFromDepot(depot, depotManifest);

                        if (result == EResult.OK)
                        {
                            anyFilesDownloaded = true;
                        }
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Notify($"Depot Processor {depot.DepotId}", e);
                    }

                    return (DepotID: depot.DepotId, BranchName: depot.BranchName, result);
                });

                processTasks.Add(task);
            }

            if (!anyFilesDownloaded && !willDownloadFiles)
            {
                foreach (var task in processTasks)
                {
                    _ = task.ContinueWith(result =>
                    {
                        RemoveLock(result.Result.DepotID, result.Result.BranchName);
                    }, TaskManager.TaskCancellationToken.Token);
                }

                await Task.WhenAll(processTasks).ConfigureAwait(false);

                return;
            }

            await Task.WhenAll(processTasks).ConfigureAwait(false);

            Log.WriteDebug(nameof(DepotProcessor), $"{depots.Count} depot downloads finished for app {appID}");

            lock (UpdateScriptLock)
            {
                foreach (var depot in depots)
                {
                    if (depot.Result == EResult.OK)
                    {
                        RunUpdateScript(UpdateScript, $"{depot.DepotId} no-git");
                    }
                    else if (depot.Result != EResult.Ignored)
                    {
                        Log.WriteWarn(nameof(DepotProcessor), $"Download failed for {depot.DepotId}: {depot.Result}");

                        RemoveErroredServer(depot.Server);

                        // Mark this depot for redownload
                        using var db = Database.Get();
                        db.Execute("UPDATE `DepotBranches` SET `LastManifestID` = 0 WHERE `DepotID` = @DepotID AND `BranchName` = @BranchName", new { DepotID = depot.DepotId, depot.BranchName });

                        if (depot.IsPreferredBranch)
                        {
                            db.Execute("UPDATE `Depots` SET `LastManifestID` = 0 WHERE `DepotID` = @DepotID", new { DepotID = depot.DepotId });
                        }
                    }

                    RemoveLock(depot.DepotId, depot.BranchName);
                }

                // Only commit changes if all depots downloaded
                if (processTasks.All(x => x.Result.Result == EResult.OK || x.Result.Result == EResult.Ignored))
                {
                    if (!RunUpdateScriptForApp(appID, depots[0].BuildId))
                    {
                        RunUpdateScript(UpdateScript, "0");
                    }
                }
                else
                {
                    Log.WriteDebug(nameof(DepotProcessor), $"Reprocessing the app {appID} because some files failed to download");

                    IRC.Instance.SendOps($"{Colors.OLIVE}[{Steam.GetAppName(appID)}]{Colors.NORMAL} Reprocessing the app due to download failures");

                    JobManager.AddJob(() => Steam.Instance.Apps.PICSGetAccessTokens(appID, null));
                }
            }
        }

        private static bool RunUpdateScript(string script, string arg)
        {
            if (!File.Exists(script))
            {
                return false;
            }

            Log.WriteDebug(nameof(DepotProcessor), $"Running update script: {script} {arg}");

            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = script,
                    Arguments = arg,
                }
            };
            process.Start();
            process.WaitForExit(120000);

            return true;
        }

        private static bool RunUpdateScriptForApp(uint appID, int buildID)
        {
            var downloadFolder = FileDownloader.GetAppDownloadFolder(appID);

            if (downloadFolder == null)
            {
                return false;
            }

            var updateScript = Path.Combine(Application.Path, "files", downloadFolder, "update.sh");

            return RunUpdateScript(updateScript, buildID.ToString());
        }

        private static async Task<(uint, string, EResult)> ProcessDepotAfterDownload(ManifestJob request, DepotManifest depotManifest)
        {
            await using var db = await Database.GetConnectionAsync();
            await using var transaction = await db.BeginTransactionAsync();

            var result = await ProcessDepotAfterDownload(db, transaction, request, depotManifest);
            await transaction.CommitAsync();

            return (request.DepotId, request.BranchName, result);
        }

        private static async Task<EResult> ProcessDepotAfterDownload(IDbConnection db, IDbTransaction transaction, ManifestJob request, DepotManifest depotManifest)
        {
            var filesOld = (await db.QueryAsync<DepotFile>(
                "SELECT `BranchName`, `File`, `Hash`, `Size`, `Flags` FROM `DepotsFiles` WHERE `DepotID` = @DepotID AND `BranchName` = @BranchName",
                new { DepotID = request.DepotId, request.BranchName },
                transaction)).ToDictionary(x => x.File, x => x);
            var filesAdded = new List<DepotFile>();
            var currentManifestFilenamesEncrypted = depotManifest.FilenamesEncrypted;

            if (currentManifestFilenamesEncrypted && request.DepotKey != null)
            {
                var sampleFile = depotManifest.Files.FirstOrDefault();

                if (sampleFile == null || TryDecryptFilename(sampleFile.FileName.Replace("\n", string.Empty), request.DepotKey, out _))
                {
                    currentManifestFilenamesEncrypted = false;
                }
                else
                {
                    Log.WriteWarn(nameof(DepotProcessor), $"Failed to decrypt filenames for depot {request.DepotId} with the current depot key");
                }
            }

            var shouldHistorize = filesOld.Count > 0 && !currentManifestFilenamesEncrypted; // Don't historize file additions if we didn't have any data before

            if (request.StoredFilenamesEncrypted && !currentManifestFilenamesEncrypted && request.DepotKey != null)
            {
                Log.WriteInfo(nameof(DepotProcessor), $"Depot {request.DepotId} will decrypt stored filenames");

                var decryptedFilesOld = new Dictionary<string, DepotFile>();

                foreach (var file in filesOld.Values)
                {
                    var oldFile = file.File;
                    file.File = DecryptFilename(oldFile, request.DepotKey);

                    decryptedFilesOld.Add(file.File, file);

                    await db.ExecuteAsync("UPDATE `DepotsFiles` SET `File` = @File WHERE `DepotID` = @DepotID AND `BranchName` = @BranchName AND `File` = @OldFile", new
                    {
                        DepotID = request.DepotId,
                        request.BranchName,
                        file.File,
                        OldFile = oldFile
                    }, transaction);
                }

                await MakeHistory(db, transaction, request, string.Empty, "files_decrypted");

                filesOld = decryptedFilesOld;
            }

            foreach (var file in depotManifest.Files.OrderByDescending(x => x.FileName))
            {
                var name = GetStoredFileName(file.FileName, depotManifest.FilenamesEncrypted, currentManifestFilenamesEncrypted ? null : request.DepotKey);

                byte[] hash = null;

                // Store empty hashes as NULL (e.g. an empty file)
                if ((file.Flags & EDepotFileFlag.Directory) == 0 && file.FileHash.Length > 0 && file.FileHash.Any(c => c != 0))
                {
                    hash = file.FileHash;
                }

                // Limit path names to 260 characters (default windows max length)
                // File column is varchar(260) and not higher to prevent reducing performance
                // See https://stackoverflow.com/questions/1962310/importance-of-varchar-length-in-mysql-table/1962329#1962329
                // Until 2019 there hasn't been a single file that went over this limit, so far there has been only one
                // game with a big node_modules path, so we're safeguarding by limiting it.
                if (name.Length > 260)
                {
                    if (currentManifestFilenamesEncrypted)
                    {
                        continue;
                    }

                    using var sha = SHA1.Create();
                    var nameHash = Utils.ByteArrayToString(sha.ComputeHash(Encoding.UTF8.GetBytes(name)));
                    name = $"{{SteamDB file name is too long}}/{nameHash}/...{name.Substring(name.Length - 150)}";
                }

                if (filesOld.ContainsKey(name))
                {
                    var oldFile = filesOld[name];
                    var updateFile = false;

                    if (oldFile.Size != file.TotalSize || !Utils.ByteArrayEquals(hash, oldFile.Hash))
                    {
                        await MakeHistory(db, transaction, request, name, "modified", oldFile.Size, file.TotalSize);

                        updateFile = true;
                    }

                    if (oldFile.Flags != file.Flags)
                    {
                        await MakeHistory(db, transaction, request, name, "modified_flags", (ulong)oldFile.Flags, (ulong)file.Flags);

                        updateFile = true;
                    }

                    if (updateFile)
                    {
                        await db.ExecuteAsync("UPDATE `DepotsFiles` SET `Hash` = @Hash, `Size` = @Size, `Flags` = @Flags WHERE `DepotID` = @DepotID AND `BranchName` = @BranchName AND `File` = @File", new DepotFile
                        {
                            DepotID = request.DepotId,
                            BranchName = request.BranchName,
                            File = name,
                            Hash = hash,
                            Size = file.TotalSize,
                            Flags = file.Flags
                        }, transaction);
                    }

                    filesOld.Remove(name);
                }
                else
                {
                    // We want to historize modifications first, and only then deletions and additions
                    filesAdded.Add(new DepotFile
                    {
                        DepotID = request.DepotId,
                        BranchName = request.BranchName,
                        Hash = hash,
                        File = name,
                        Size = file.TotalSize,
                        Flags = file.Flags
                    });
                }
            }

            if (filesOld.Count > 0)
            {
                // Chunk file deletion queries so it doesn't go over max_allowed_packet
                var filesOldChunks = filesOld.Select(x => x.Value.File).Split(1000);

                foreach (var filesOldChunk in filesOldChunks)
                {
                    await db.ExecuteAsync("DELETE FROM `DepotsFiles` WHERE `DepotID` = @DepotID AND `BranchName` = @BranchName AND `File` IN @Files",
                        new
                        {
                            DepotID = request.DepotId,
                            request.BranchName,
                            Files = filesOldChunk,
                        }, transaction);
                }

                if (shouldHistorize)
                {
                    await db.ExecuteAsync(HistoryQuery, filesOld.Select(x => new DepotHistory
                    {
                        DepotID = request.DepotId,
                        BranchName = request.BranchName,
                        ManifestID = request.ManifestId,
                        ChangeID = request.ChangeNumber,
                        Action = "removed",
                        File = x.Value.File,
                        OldValue = x.Value.Size
                    }), transaction);
                }
            }

            if (filesAdded.Count > 0)
            {
                await db.ExecuteAsync("INSERT INTO `DepotsFiles` (`DepotID`, `BranchName`, `File`, `Hash`, `Size`, `Flags`) VALUES (@DepotID, @BranchName, @File, @Hash, @Size, @Flags)", filesAdded, transaction);

                if (shouldHistorize)
                {
                    await db.ExecuteAsync(HistoryQuery, filesAdded.Select(x => new DepotHistory
                    {
                        DepotID = request.DepotId,
                        BranchName = request.BranchName,
                        ManifestID = request.ManifestId,
                        ChangeID = request.ChangeNumber,
                        Action = "added",
                        File = x.File,
                        NewValue = x.Size
                    }), transaction);
                }
            }

            await db.ExecuteAsync(
                request.LastManifestId == request.ManifestId ?
                    "UPDATE `DepotBranches` SET `LastManifestID` = @ManifestID, `ManifestDate` = @ManifestDate, `FilenamesEncrypted` = @FilenamesEncrypted, `SizeOriginal` = @SizeOriginal, `SizeCompressed` = @SizeCompressed WHERE `DepotID` = @DepotID AND `BranchName` = @BranchName" :
                    "UPDATE `DepotBranches` SET `LastManifestID` = @ManifestID, `ManifestDate` = @ManifestDate, `FilenamesEncrypted` = @FilenamesEncrypted, `SizeOriginal` = @SizeOriginal, `SizeCompressed` = @SizeCompressed, `LastUpdated` = CURRENT_TIMESTAMP() WHERE `DepotID` = @DepotID AND `BranchName` = @BranchName",
                new
                {
                    DepotID = request.DepotId,
                    request.BranchName,
                    ManifestID = request.ManifestId,
                    FilenamesEncrypted = currentManifestFilenamesEncrypted,
                    ManifestDate = depotManifest.CreationTime,
                    SizeOriginal = depotManifest.TotalUncompressedSize,
                    SizeCompressed = depotManifest.TotalCompressedSize,
                }, transaction);

            if (request.IsPreferredBranch)
            {
                await db.ExecuteAsync(
                    request.LastManifestId == request.ManifestId ?
                        "UPDATE `Depots` SET `PreferredBranchName` = @BranchName, `LastManifestID` = @ManifestID, `ManifestDate` = @ManifestDate, `FilenamesEncrypted` = @FilenamesEncrypted, `SizeOriginal` = @SizeOriginal, `SizeCompressed` = @SizeCompressed WHERE `DepotID` = @DepotID" :
                        "UPDATE `Depots` SET `PreferredBranchName` = @BranchName, `LastManifestID` = @ManifestID, `ManifestDate` = @ManifestDate, `FilenamesEncrypted` = @FilenamesEncrypted, `SizeOriginal` = @SizeOriginal, `SizeCompressed` = @SizeCompressed, `LastUpdated` = CURRENT_TIMESTAMP() WHERE `DepotID` = @DepotID",
                    new
                    {
                        DepotID = request.DepotId,
                        request.BranchName,
                        ManifestID = request.ManifestId,
                        FilenamesEncrypted = currentManifestFilenamesEncrypted,
                        ManifestDate = depotManifest.CreationTime,
                        SizeOriginal = depotManifest.TotalUncompressedSize,
                        SizeCompressed = depotManifest.TotalCompressedSize,
                    },
                    transaction);
            }

            return EResult.OK;
        }

        internal static string GetStoredFileName(string name, bool filenamesEncrypted, byte[] depotKey)
        {
            if (!filenamesEncrypted)
            {
                return name.Replace('\\', '/');
            }

            name = name.Replace("\n", string.Empty);

            return depotKey != null && TryDecryptFilename(name, depotKey, out var decryptedName)
                ? decryptedName
                : name;
        }

        internal static bool TryDecryptFilename(string name, byte[] depotKey, out string decryptedName)
        {
            decryptedName = null;

            if (depotKey == null || string.IsNullOrEmpty(name))
            {
                return false;
            }

            try
            {
                decryptedName = DecryptFilename(name, depotKey);

                return !string.IsNullOrWhiteSpace(decryptedName);
            }
            catch (FormatException)
            {
                return false;
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        private static string DecryptFilename(string name, byte[] depotKey)
        {
            var encryptedFilename = Convert.FromBase64String(name);
            var decryptedFilename = CryptoHelper.SymmetricDecrypt(encryptedFilename, depotKey);

            return Encoding.UTF8.GetString(decryptedFilename).TrimEnd(new[] { '\0' }).Replace('\\', '/');
        }

        private async Task ProcessDlcNames(uint parentAppId, uint changeNumber, Dictionary<uint, string> dlcNames)
        {
            // TODO: Track name changes?
            await using var db = await Database.GetConnectionAsync();
            var dlcApps = await db.QueryAsync<App>("SELECT `AppID` FROM `Apps` WHERE `AppID` IN @Ids AND `LastKnownName` = \"\"", new { Ids = dlcNames.Keys });
            var parentKey = KeyNameCache.GetAppKeyID("common_parent");

            foreach (var dlcApp in dlcApps)
            {
                Log.WriteInfo(nameof(DepotProcessor), $"Got a name for app {dlcApp.AppID} from parent app {parentAppId}");

                var name = dlcNames[dlcApp.AppID];
                name = DepotNameStart.Replace(name, string.Empty);
                name = DepotNameEnd.Replace(name, string.Empty);

                var dlcAppIdEnding = $" ({dlcApp.AppID})";

                if (name.EndsWith(dlcAppIdEnding))
                {
                    name = name.Substring(0, name.Length - dlcAppIdEnding.Length);
                }

                name = name.Trim();

                await db.ExecuteAsync("UPDATE `Apps` SET `LastKnownName` = @AppName WHERE `AppID` = @AppID", new
                {
                    dlcApp.AppID,
                    AppName = name,
                });

                await db.ExecuteAsync("INSERT INTO `AppsInfo` (`AppID`, `Key`, `Value`) VALUES (@AppID, @Key, @Value) ON DUPLICATE KEY UPDATE `AppID` = `AppID`", new
                {
                    dlcApp.AppID,
                    Key = parentKey,
                    Value = parentAppId.ToString(),
                });

                await db.ExecuteAsync(AppProcessor.HistoryQuery,
                    new PICSHistory
                    {
                        ID = dlcApp.AppID,
                        ChangeID = changeNumber,
                        Key = SteamDB.DatabaseNameType,
                        NewValue = name,
                        OldValue = "Depot name",
                        Action = "created_info",
                    }
                );

                await db.ExecuteAsync(AppProcessor.HistoryQuery,
                    new PICSHistory
                    {
                        ID = dlcApp.AppID,
                        ChangeID = changeNumber,
                        Key = parentKey,
                        NewValue = parentAppId.ToString(),
                        Action = "created_key",
                    }
                );
            }
        }

        private static Task MakeHistory(IDbConnection db, IDbTransaction transaction, ManifestJob request, string file, string action, ulong oldValue = 0, ulong newValue = 0)
        {
            return db.ExecuteAsync(HistoryQuery,
                new DepotHistory
                {
                    DepotID = request.DepotId,
                    BranchName = request.BranchName,
                    ManifestID = request.ManifestId,
                    ChangeID = request.ChangeNumber,
                    Action = action,
                    File = file,
                    OldValue = oldValue,
                    NewValue = newValue
                },
                transaction
            );
        }

        private void RemoveLock(uint depotID, string branchName)
        {
            var lockKey = GetDepotLockKey(depotID, branchName);

            lock (DepotLocks)
            {
                if (DepotLocks.Remove(lockKey))
                {
                    Log.WriteInfo(nameof(DepotProcessor), $"Processed depot {depotID} ({branchName}, {DepotLocks.Count} depot locks left)");
                }
            }
        }

        private void RemoveErroredServer(Server server)
        {
            if (CDNServers.Count < 10)
            {
                // Let the watchdog update the server list in next check
                LastServerRefreshTime = DateTime.MinValue;
            }

            Log.WriteWarn(nameof(DepotProcessor), $"Removing {server} due to a download error");

            CDNServers.Remove(server);

            // Always have one server in the list in case we run out
            if (CDNServers.Count == 0)
            {
                CDNServers.Add(new DnsEndPoint(DefaultServer, 80));
            }
        }

        private Server GetContentServer()
        {
            var i = Utils.NextRandom(CDNServers.Count);

            return CDNServers[i];
        }
    }
}

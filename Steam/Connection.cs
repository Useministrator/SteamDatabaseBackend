/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using Dapper;
using SteamKit2;
using SteamKit2.Authentication;

namespace SteamDatabaseBackend
{
    internal class Connection : SteamHandler, IDisposable
    {
        private const int MAX_RETRY_DELAY = 300;
        private const int TOKEN_RETRY_DELAY = 30;
        private const int GUARD_CODE_RETRY_DELAY = 60;
        private const int MAX_GUARD_CODE_DELAY = 600;
        private const int RATE_LIMIT_RETRY_DELAY = 300;
        private const int MAX_RATE_LIMIT_DELAY = 1800;
        private const string DefaultSteamGuardCodeFile = "/run/steamdatabasebackend/steam-guard.env";

        private static readonly HashSet<EResult> StoredTokenInvalidResults =
        [
            EResult.InvalidPassword,
            EResult.AccessDenied,
            EResult.InvalidState,
            EResult.Revoked,
            EResult.Expired,
            EResult.InvalidSignature,
        ];

        private static readonly HashSet<EResult> RateLimitedResults =
        [
            EResult.RateLimitExceeded,
            EResult.AccountLoginDeniedThrottle,
            EResult.WGNetworkSendExceeded,
        ];

        private static readonly HashSet<EResult> GuardCodeResults =
        [
            EResult.AccountLogonDenied,
            EResult.AccountLoginDeniedNeedTwoFactor,
        ];

        private static readonly HashSet<EResult> ExpiredGuardDataResults =
        [
            EResult.Expired,
            EResult.InvalidState,
            EResult.InvalidSignature,
        ];

        private static Connection Current;

        private sealed class EnvironmentAwareAuthenticator : IAuthenticator
        {
            private enum GuardCodeKind
            {
                Email,
                Device,
            }

            private sealed class GuardCodeState
            {
                public string Code { get; init; }
                public bool IsNonInteractive { get; init; }
                public string SourceDescription { get; init; }
            }

            private static readonly string[] EmailCodeEnvironmentVariables =
            [
                "STEAM_GUARD_CODE",
                "STEAM_EMAIL_CODE",
            ];

            private static readonly string[] DeviceCodeEnvironmentVariables =
            [
                "STEAM_GUARD_CODE",
                "STEAM_TWO_FACTOR_CODE",
            ];

            private static readonly object Sync = new();
            private static readonly Dictionary<GuardCodeKind, GuardCodeState> LastIssuedCodes = new();
            private static readonly HashSet<string> RejectedCodes = new(StringComparer.Ordinal);

            public Task<bool> AcceptDeviceConfirmationAsync() => Task.FromResult(false);

            public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
            {
                var prompt = previousCodeWasIncorrect
                    ? "STEAM GUARD! The previous 2 factor auth code was incorrect. Please enter a new code from your authenticator app: "
                    : "STEAM GUARD! Please enter your 2 factor auth code from your authenticator app: ";

                return Task.FromResult(ReadCode(GuardCodeKind.Device, DeviceCodeEnvironmentVariables, prompt, previousCodeWasIncorrect));
            }

            public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
            {
                var prompt = previousCodeWasIncorrect
                    ? $"STEAM GUARD! The previous email code was incorrect. Please enter the auth code sent to the email at {email}: "
                    : $"STEAM GUARD! Please enter the auth code sent to the email at {email}: ";

                return Task.FromResult(ReadCode(GuardCodeKind.Email, EmailCodeEnvironmentVariables, prompt, previousCodeWasIncorrect));
            }

            public static string ReadEmailCode(string email, bool previousCodeWasIncorrect)
            {
                var prompt = previousCodeWasIncorrect
                    ? $"STEAM GUARD! The previous email code was incorrect. Please enter the auth code sent to the email at {email}: "
                    : $"STEAM GUARD! Please enter the auth code sent to the email at {email}: ";

                return ReadCode(GuardCodeKind.Email, EmailCodeEnvironmentVariables, prompt, previousCodeWasIncorrect);
            }

            public static string ReadDeviceCode(bool previousCodeWasIncorrect)
            {
                var prompt = previousCodeWasIncorrect
                    ? "STEAM GUARD! The previous 2 factor auth code was incorrect. Please enter a new code from your authenticator app: "
                    : "STEAM GUARD! Please enter your 2 factor auth code from your authenticator app: ";

                return ReadCode(GuardCodeKind.Device, DeviceCodeEnvironmentVariables, prompt, previousCodeWasIncorrect);
            }

            private static string ReadCode(GuardCodeKind kind, string[] variableNames, string prompt, bool previousCodeWasIncorrect)
            {
                lock (Sync)
                {
                    if (previousCodeWasIncorrect)
                    {
                        RejectLastIssuedCode(kind);
                    }

                    var sourcedCode = TryReadCodeFromFile(variableNames);

                    if (!string.IsNullOrWhiteSpace(sourcedCode))
                    {
                        LastIssuedCodes[kind] = new GuardCodeState
                        {
                            Code = sourcedCode,
                            IsNonInteractive = true,
                            SourceDescription = "steam guard code file",
                        };

                        return sourcedCode;
                    }

                    sourcedCode = TryReadCodeFromEnvironment(variableNames);

                    if (!string.IsNullOrWhiteSpace(sourcedCode))
                    {
                        LastIssuedCodes[kind] = new GuardCodeState
                        {
                            Code = sourcedCode,
                            IsNonInteractive = true,
                            SourceDescription = "environment variable",
                        };

                        return sourcedCode;
                    }
                }

                if (!IsInteractiveConsoleAvailable())
                {
                    Log.WriteWarn(nameof(Connection), "Steam Guard code is required, but no fresh code was found in the steam guard code file or environment variables");
                    return null;
                }

                Console.Write(prompt);

                var consoleCode = Console.ReadLine()?.Trim();

                if (string.IsNullOrWhiteSpace(consoleCode))
                {
                    return null;
                }

                lock (Sync)
                {
                    LastIssuedCodes[kind] = new GuardCodeState
                    {
                        Code = consoleCode,
                        IsNonInteractive = false,
                        SourceDescription = "interactive console input",
                    };
                }

                return consoleCode;
            }

            private static void RejectLastIssuedCode(GuardCodeKind kind)
            {
                if (!LastIssuedCodes.TryGetValue(kind, out var state))
                {
                    return;
                }

                LastIssuedCodes.Remove(kind);

                if (!state.IsNonInteractive || string.IsNullOrWhiteSpace(state.Code))
                {
                    return;
                }

                RejectedCodes.Add(state.Code);
                Log.WriteWarn(nameof(Connection), $"Previously supplied Steam Guard code from {state.SourceDescription} was rejected; waiting for a fresh code");
            }

            private static string TryReadCodeFromEnvironment(IEnumerable<string> variableNames)
            {
                foreach (var variableName in variableNames)
                {
                    var value = Environment.GetEnvironmentVariable(variableName)?.Trim();

                    if (string.IsNullOrWhiteSpace(value) || RejectedCodes.Contains(value))
                    {
                        continue;
                    }

                    Environment.SetEnvironmentVariable(variableName, null);

                    return value;
                }

                return null;
            }

            private static string TryReadCodeFromFile(IEnumerable<string> variableNames)
            {
                var path = Environment.GetEnvironmentVariable("STEAM_GUARD_FILE");

                if (string.IsNullOrWhiteSpace(path))
                {
                    path = Environment.GetEnvironmentVariable("STEAM_GUARD_CODE_FILE");
                }

                if (string.IsNullOrWhiteSpace(path) && !OperatingSystem.IsWindows())
                {
                    path = DefaultSteamGuardCodeFile;
                }

                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return null;
                }

                try
                {
                    var values = File
                        .ReadAllLines(path)
                        .Select(line => line.Trim())
                        .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
                        .Select(line => line.Split('=', 2))
                        .Where(parts => parts.Length == 2)
                        .ToDictionary(
                            parts => parts[0].Trim(),
                            parts => parts[1].Trim().Trim('"'),
                            StringComparer.OrdinalIgnoreCase
                        );

                    foreach (var variableName in variableNames)
                    {
                        if (values.TryGetValue(variableName, out var value)
                            && !string.IsNullOrWhiteSpace(value)
                            && !RejectedCodes.Contains(value))
                        {
                            TryDeleteSteamGuardCodeFile(path);

                            return value;
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.WriteWarn(nameof(Connection), $"Unable to read steam guard code file '{path}': {e.Message}");
                }

                return null;
            }

            private static void TryDeleteSteamGuardCodeFile(string path)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception e)
                {
                    Log.WriteWarn(nameof(Connection), $"Unable to delete steam guard code file '{path}' after reading it: {e.Message}");
                }
            }

            private static bool IsInteractiveConsoleAvailable()
            {
                try
                {
                    return !Console.IsInputRedirected;
                }
                catch
                {
                    return false;
                }
            }
        }

        public const uint RETRY_DELAY = 15;

        public static DateTime LastSuccessfulLogin { get; private set; }

        private Timer ReconnectionTimer;

        private string AuthCode;
        private bool IsTwoFactor;
        private bool CurrentAttemptUsedStoredToken;
        private bool CredentialRecoveryRequested;
        private bool PreviousAttemptUsedEmailCode;
        private bool PreviousAttemptUsedTwoFactorCode;
        private int ReconnectAttempts;
        private DateTime? NextReconnectAt;

        public Connection(CallbackManager manager)
        {
            Current = this;
            ReconnectionTimer = new Timer
            {
                AutoReset = false
            };
            ReconnectionTimer.Elapsed += Reconnect;
            ReconnectionTimer.Interval = TimeSpan.FromSeconds(RETRY_DELAY).TotalMilliseconds;

            manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
            manager.Subscribe<SteamUser.SessionTokenCallback>(OnSessionToken);
        }

        public void Dispose()
        {
            if (Current == this)
            {
                Current = null;
            }

            if (ReconnectionTimer != null)
            {
                ReconnectionTimer.Dispose();
                ReconnectionTimer = null;
            }
        }

        public static void Reconnect(object sender, ElapsedEventArgs e)
        {
            Current?.ReconnectInternal();
        }

        public static void ScheduleReconnect(string reason, EResult? result = null)
        {
            Current?.ScheduleReconnectInternal(reason, result);
        }

        public static bool IsStoredRefreshTokenInvalidResult(EResult result)
        {
            return StoredTokenInvalidResults.Contains(result);
        }

        public static Task RecoverFromExpiredRefreshTokenAsync(string reason, EResult? result = null, bool disconnectClient = false)
        {
            return Current?.RecoverFromExpiredRefreshTokenInternalAsync(reason, result, disconnectClient) ?? Task.CompletedTask;
        }

        private void ReconnectInternal()
        {
            if (Steam.Instance.Client.IsConnected)
            {
                Log.WriteDebug(nameof(Steam), "Reconnect requested, but the Steam client is already connected");
                return;
            }

            Log.WriteDebug(nameof(Steam), "Reconnecting...");

            Steam.Instance.Client.Connect();
        }

        private void ScheduleReconnectInternal(string reason, EResult? result = null)
        {
            if (!Steam.Instance.IsRunning || ReconnectionTimer == null)
            {
                return;
            }

            var candidateAttempt = ReconnectAttempts + 1;
            var delay = GetReconnectDelay(candidateAttempt, result);
            var reconnectAt = DateTime.UtcNow.Add(delay);

            lock (ReconnectionTimer)
            {
                if (ReconnectionTimer.Enabled && NextReconnectAt.HasValue && NextReconnectAt.Value <= reconnectAt)
                {
                    return;
                }

                ReconnectAttempts = candidateAttempt;
                ReconnectionTimer.Stop();
                ReconnectionTimer.Interval = Math.Max(1000, delay.TotalMilliseconds);
                NextReconnectAt = reconnectAt;
                ReconnectionTimer.Start();
            }

            var resultText = result.HasValue ? $" ({result.Value})" : string.Empty;
            Log.WriteInfo(nameof(Steam), $"Retrying Steam connection in {delay.TotalSeconds:0}s: {reason}{resultText}");
        }

        private static TimeSpan GetReconnectDelay(int attempt, EResult? result)
        {
            var baseDelay = RETRY_DELAY;
            var maxDelay = MAX_RETRY_DELAY;

            if (result.HasValue && RateLimitedResults.Contains(result.Value))
            {
                baseDelay = RATE_LIMIT_RETRY_DELAY;
                maxDelay = MAX_RATE_LIMIT_DELAY;
            }
            else if (result.HasValue && GuardCodeResults.Contains(result.Value))
            {
                baseDelay = GUARD_CODE_RETRY_DELAY;
                maxDelay = MAX_GUARD_CODE_DELAY;
            }
            else if (result.HasValue && StoredTokenInvalidResults.Contains(result.Value))
            {
                baseDelay = TOKEN_RETRY_DELAY;
            }

            var exponent = Math.Min(attempt - 1, 6);
            var delaySeconds = Math.Min(baseDelay * Math.Pow(2, exponent), maxDelay);
            var jitterSeconds = Utils.NextRandom(6);

            return TimeSpan.FromSeconds(delaySeconds + jitterSeconds);
        }

        private async Task<string> CreateRefreshTokenFromCredentialsAsync(string guardData, bool allowGuardDataRetry = true)
        {
            try
            {
                var authSession = await Steam.Instance.Client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                {
                    Username = Settings.Current.Steam.Username,
                    Password = Settings.Current.Steam.Password,
                    IsPersistentSession = true,
                    GuardData = guardData,
                    Authenticator = new EnvironmentAwareAuthenticator(),
                });

                var result = await authSession.PollingWaitForResultAsync();

                if (string.IsNullOrWhiteSpace(result.RefreshToken))
                {
                    Log.WriteWarn(nameof(Steam), "Steam authentication succeeded but did not return a refresh token");
                    ScheduleReconnectInternal("Steam did not return a refresh token");
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(result.NewGuardData))
                {
                    await LocalConfig.Update("backend.guarddata", result.NewGuardData);
                }

                await LocalConfig.Update("backend.loginkey", result.RefreshToken);
                CredentialRecoveryRequested = false;

                return result.RefreshToken;
            }
            catch (AuthenticationException e) when (allowGuardDataRetry && !string.IsNullOrWhiteSpace(guardData) && ExpiredGuardDataResults.Contains(e.Result))
            {
                Log.WriteWarn(nameof(Steam), $"Stored Steam guard data was rejected with {e.Result}, clearing it and retrying credential auth once");

                await ClearStoredCredentialsAsync(clearRefreshToken: false, clearGuardData: true);

                return await CreateRefreshTokenFromCredentialsAsync(null, allowGuardDataRetry: false);
            }
            catch (AuthenticationException e)
            {
                Log.WriteWarn(nameof(Steam), $"Credential authentication failed: {e.Result}");
                ScheduleReconnectInternal("credential authentication failed", e.Result);

                return null;
            }
            catch (Exception e)
            {
                Log.WriteError(nameof(Steam), $"Credential authentication failed with exception: {e.Message}");
                ScheduleReconnectInternal("credential authentication failed");

                return null;
            }
        }

        private async Task RecoverFromExpiredRefreshTokenInternalAsync(string reason, EResult? result, bool disconnectClient)
        {
            if (CredentialRecoveryRequested)
            {
                return;
            }

            CredentialRecoveryRequested = true;

            await ClearStoredCredentialsAsync(clearRefreshToken: true, clearGuardData: false);
            ScheduleReconnectInternal(reason, result ?? EResult.Expired);

            if (disconnectClient && Steam.Instance.Client.IsConnected)
            {
                Log.WriteWarn(nameof(Steam), $"{reason}. Disconnecting current Steam session to request a fresh login token");
                Steam.Instance.Client.Disconnect();
            }
        }

        private static async Task ClearStoredCredentialsAsync(bool clearRefreshToken, bool clearGuardData)
        {
            var keys = new List<string>();

            if (clearRefreshToken)
            {
                keys.Add("backend.loginkey");
            }

            if (clearGuardData)
            {
                keys.Add("backend.guarddata");
            }

            if (keys.Count == 0)
            {
                return;
            }

            await using var db = await Database.GetConnectionAsync();
            await db.ExecuteAsync("DELETE FROM `LocalConfig` WHERE `ConfigKey` IN @Keys", new { Keys = keys });
        }

        private async void OnConnected(SteamClient.ConnectedCallback callback)
        {
            ReconnectionTimer.Stop();
            NextReconnectAt = null;
            CurrentAttemptUsedStoredToken = false;

            try
            {
                await using var db = await Database.GetConnectionAsync();
                var config = (await db.QueryAsync<(string, string)>(
                    "SELECT `ConfigKey`, `Value` FROM `LocalConfig` WHERE `ConfigKey` IN ('backend.loginkey', 'backend.guarddata')"
                )).ToDictionary(x => x.Item1, x => x.Item2);

                Log.WriteInfo(nameof(Steam), "Connected, logging in...");

                if (Settings.Current.Steam.Username == "anonymous")
                {
                    Log.WriteInfo(nameof(Steam), "Using an anonymous account");

                    Steam.Instance.User.LogOnAnonymous();

                    return;
                }

                var accessToken = config.TryGetValue("backend.loginkey", out var loginToken) ? loginToken : null;
                CurrentAttemptUsedStoredToken = !string.IsNullOrWhiteSpace(accessToken) && accessToken != "0";

                if (!CurrentAttemptUsedStoredToken)
                {
                    accessToken = await CreateRefreshTokenFromCredentialsAsync(config.TryGetValue("backend.guarddata", out var guardData) ? guardData : null);

                    if (string.IsNullOrWhiteSpace(accessToken))
                    {
                        return;
                    }
                }

                var logonDetails = new SteamUser.LogOnDetails
                {
                    AccessToken = accessToken,
                    Username = Settings.Current.Steam.Username,
                    Password = accessToken == null ? Settings.Current.Steam.Password : null,
                    AuthCode = IsTwoFactor ? null : AuthCode,
                    TwoFactorCode = IsTwoFactor ? AuthCode : null,
                    ShouldRememberPassword = true,
                    LoginID = 0x78_50_61_77,
                };

                PreviousAttemptUsedEmailCode = !IsTwoFactor && !string.IsNullOrWhiteSpace(AuthCode);
                PreviousAttemptUsedTwoFactorCode = IsTwoFactor && !string.IsNullOrWhiteSpace(AuthCode);

                Steam.Instance.User.LogOn(logonDetails);
            }
            catch (Exception e)
            {
                Log.WriteError(nameof(Steam), $"Exception while preparing Steam logon: {e.Message}");
                ScheduleReconnectInternal("exception while preparing Steam logon");
            }
            finally
            {
                AuthCode = null;
            }
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            Steam.Instance.PICSChanges.StopTick();

            if (!Steam.Instance.IsRunning)
            {
                Log.WriteInfo(nameof(Steam), "Disconnected from Steam");

                return;
            }
            
            Log.WriteInfo(nameof(Steam), $"Disconnected from Steam{(callback.UserInitiated ? " (user initiated)" : string.Empty)}");

            ScheduleReconnectInternal("disconnected from Steam");
        }

        private async void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result == EResult.AccountLogonDenied)
            {
                IsTwoFactor = false;
                AuthCode = EnvironmentAwareAuthenticator.ReadEmailCode(callback.EmailDomain, PreviousAttemptUsedEmailCode);
                PreviousAttemptUsedEmailCode = false;
                PreviousAttemptUsedTwoFactorCode = false;
                ScheduleReconnectInternal("steam guard email code requested", callback.Result);

                return;
            }
            else if (callback.Result == EResult.AccountLoginDeniedNeedTwoFactor)
            {
                IsTwoFactor = true;
                AuthCode = EnvironmentAwareAuthenticator.ReadDeviceCode(PreviousAttemptUsedTwoFactorCode);
                PreviousAttemptUsedEmailCode = false;
                PreviousAttemptUsedTwoFactorCode = false;
                ScheduleReconnectInternal("steam guard 2fa code requested", callback.Result);

                return;
            }

            if (callback.Result != EResult.OK)
            {
                if (CurrentAttemptUsedStoredToken && StoredTokenInvalidResults.Contains(callback.Result))
                {
                    await RecoverFromExpiredRefreshTokenInternalAsync("stored Steam login token was rejected", callback.Result, disconnectClient: false);
                }

                Log.WriteInfo(nameof(Steam), $"Failed to login: {callback.Result}");
                ScheduleReconnectInternal("Steam login failed", callback.Result);
                return;
            }

            ReconnectAttempts = 0;
            NextReconnectAt = null;
            CredentialRecoveryRequested = false;
            CurrentAttemptUsedStoredToken = false;
            PreviousAttemptUsedEmailCode = false;
            PreviousAttemptUsedTwoFactorCode = false;
            LastSuccessfulLogin = DateTime.Now;
            
            Log.WriteInfo(nameof(Steam), $"Logged in, current Valve time is {callback.ServerTime:R}");

            await Steam.Instance.DepotProcessor.UpdateContentServerList();

            JobManager.RestartJobsIfAny();

            if (!Settings.IsFullRun)
            {
                Steam.Instance.PICSChanges.StartTick();
            }
            else if (Steam.Instance.PICSChanges.PreviousChangeNumber == 0)
            {
                Steam.Instance.PICSChanges.PreviousChangeNumber = 1;

                _ = TaskManager.Run(FullUpdateProcessor.PerformSync);
            }
        }

        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Log.WriteInfo(nameof(Steam), $"Logged out of Steam: {callback.Result}");
        }

        private static async void OnSessionToken(SteamUser.SessionTokenCallback callback)
        {
            Log.WriteInfo(nameof(Steam), $"Got new session token with unique id {callback.SessionToken}");

            await LocalConfig.Update("backend.sessiontoken", callback.SessionToken.ToString());

            // Steam.Instance.User.AcceptNewLoginKey(callback);
        }
    }
}

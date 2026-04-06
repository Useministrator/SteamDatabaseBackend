/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
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
        private sealed class EnvironmentAwareAuthenticator : IAuthenticator
        {
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

            public Task<bool> AcceptDeviceConfirmationAsync() => Task.FromResult(false);

            public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
            {
                var prompt = previousCodeWasIncorrect
                    ? "STEAM GUARD! The previous 2 factor auth code was incorrect. Please enter a new code from your authenticator app: "
                    : "STEAM GUARD! Please enter your 2 factor auth code from your authenticator app: ";

                return Task.FromResult(ReadCode(DeviceCodeEnvironmentVariables, prompt));
            }

            public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
            {
                var prompt = previousCodeWasIncorrect
                    ? $"STEAM GUARD! The previous email code was incorrect. Please enter the auth code sent to the email at {email}: "
                    : $"STEAM GUARD! Please enter the auth code sent to the email at {email}: ";

                return Task.FromResult(ReadCode(EmailCodeEnvironmentVariables, prompt));
            }

            private static string ReadCode(string[] variableNames, string prompt)
            {
                foreach (var variableName in variableNames)
                {
                    var value = Environment.GetEnvironmentVariable(variableName);

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        Environment.SetEnvironmentVariable(variableName, null);

                        return value.Trim();
                    }
                }

                Console.Write(prompt);

                return Console.ReadLine()?.Trim();
            }
        }

        public const uint RETRY_DELAY = 15;

        public static DateTime LastSuccessfulLogin { get; private set; }

        private Timer ReconnectionTimer;

        private string AuthCode;
        private bool IsTwoFactor;

        public Connection(CallbackManager manager)
        {
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
            if (ReconnectionTimer != null)
            {
                ReconnectionTimer.Dispose();
                ReconnectionTimer = null;
            }
        }

        public static void Reconnect(object sender, ElapsedEventArgs e)
        {
            Log.WriteDebug(nameof(Steam), "Reconnecting...");

            Steam.Instance.Client.Connect();
        }

        private async void OnConnected(SteamClient.ConnectedCallback callback)
        {
            ReconnectionTimer.Stop();

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
            if (accessToken is null or "0")
            {
                AuthSession authSession;
                authSession = await Steam.Instance.Client.Authentication.BeginAuthSessionViaCredentialsAsync(new SteamKit2.Authentication.AuthSessionDetails
                {
                    Username = Settings.Current.Steam.Username,
                    Password = Settings.Current.Steam.Password,
                    IsPersistentSession = true,
                    GuardData = config.TryGetValue("backend.guarddata", out var guardData) ? guardData : null,
                    Authenticator = new EnvironmentAwareAuthenticator(),
                });
                var result = await authSession.PollingWaitForResultAsync();
                accessToken = result.RefreshToken;

                if (!string.IsNullOrWhiteSpace(result.NewGuardData))
                {
                    await LocalConfig.Update("backend.guarddata", result.NewGuardData);
                }

                await LocalConfig.Update("backend.loginkey", accessToken);
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

            Steam.Instance.User.LogOn(logonDetails);
            AuthCode = null;
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            Steam.Instance.PICSChanges.StopTick();

            if (!Steam.Instance.IsRunning)
            {
                Log.WriteInfo(nameof(Steam), "Disconnected from Steam");

                return;
            }
            
            Log.WriteInfo(nameof(Steam), $"Disconnected from Steam. Retrying in {RETRY_DELAY} seconds... {(callback.UserInitiated ? " (user initiated)" : "")}");

            ReconnectionTimer.Start();
        }

        private async void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result == EResult.AccountLogonDenied)
            {
                Console.Write($"STEAM GUARD! Please enter the auth code sent to the email at {callback.EmailDomain}: ");

                IsTwoFactor = false;
                AuthCode = Console.ReadLine()?.Trim();

                return;
            }
            else if (callback.Result == EResult.AccountLoginDeniedNeedTwoFactor)
            {
                Console.Write("STEAM GUARD! Please enter your 2 factor auth code from your authenticator app: ");

                IsTwoFactor = true;
                AuthCode = Console.ReadLine()?.Trim();

                return;
            }

            if (callback.Result == EResult.InvalidPassword)
            {
                await using var db = await Database.GetConnectionAsync();
                await db.ExecuteAsync("DELETE FROM `LocalConfig` WHERE `ConfigKey` = 'backend.loginkey'");
            }

            if (callback.Result != EResult.OK)
            {
                Log.WriteInfo(nameof(Steam), $"Failed to login: {callback.Result}");
                return;
            }

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

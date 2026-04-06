/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using SteamKit2;
using SteamKit2.Authentication;

namespace SteamDatabaseBackend
{
    internal class WebAuth : SteamHandler
    {
        private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36";
        private const string SessionIdCookieName = "sessionid";
        private static readonly string[] WebAuthDomains = ["store.steampowered.com", "steamcommunity.com"];
        private static readonly SemaphoreSlim AuthenticationSemaphore = new SemaphoreSlim(1, 1);
        private static readonly HttpClient WebHttpClient = CreateWebHttpClient();

        public static bool IsAuthorized { get; private set; }
        private static CookieContainer Cookies = new CookieContainer();

        public WebAuth(CallbackManager manager)
        {
            manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            IsAuthorized = false;
            Cookies = new CookieContainer();

            if (callback.Result != EResult.OK)
            {
                return;
            }

            TaskManager.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                await AuthenticateUser();
            });
        }

        public static async Task<bool> AuthenticateUser()
        {
            await AuthenticationSemaphore.WaitAsync();

            try
            {
                if (IsAuthorized)
                {
                    return true;
                }

                var refreshToken = await GetRefreshTokenAsync();

                if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken == "0")
                {
                    IsAuthorized = false;

                    Log.WriteWarn(nameof(WebAuth), "Failed to authenticate: no refresh token available");

                    return false;
                }

                var steamId = Steam.Instance.Client.SteamID;
                var tokens = await Steam.Instance.Client.Authentication.GenerateAccessTokenForAppAsync(steamId, refreshToken, allowRenewal: true);

                if (string.IsNullOrWhiteSpace(tokens.AccessToken))
                {
                    IsAuthorized = false;

                    Log.WriteWarn(nameof(WebAuth), "Failed to authenticate: Steam did not return an access token");

                    return false;
                }

                if (!string.IsNullOrWhiteSpace(tokens.RefreshToken) && !string.Equals(tokens.RefreshToken, refreshToken, StringComparison.Ordinal))
                {
                    await LocalConfig.Update("backend.loginkey", tokens.RefreshToken);
                }

                Cookies = CreateCookies(steamId.ConvertToUInt64(), tokens.AccessToken);
                IsAuthorized = true;

                Log.WriteInfo(nameof(WebAuth), "Authenticated");

                return true;
            }
            catch (AuthenticationException e) when (Connection.IsStoredRefreshTokenInvalidResult(e.Result))
            {
                IsAuthorized = false;
                Cookies = new CookieContainer();

                Log.WriteWarn(nameof(WebAuth), $"Stored refresh token was rejected during web auth: {e.Result}");

                await Connection.RecoverFromExpiredRefreshTokenAsync("web auth refresh token was rejected", e.Result, disconnectClient: Steam.Instance.Client.IsConnected);

                return false;
            }
            catch (AuthenticationException e)
            {
                IsAuthorized = false;

                Log.WriteWarn(nameof(WebAuth), $"Failed to authenticate: {e.Result}");

                return false;
            }
            catch (Exception e)
            {
                IsAuthorized = false;

                Log.WriteWarn(nameof(WebAuth), $"Failed to authenticate: {e.Message}");

                return false;
            }
            finally
            {
                AuthenticationSemaphore.Release();
            }
        }

        private static CookieContainer CreateCookies(ulong steamId, string accessToken)
        {
            var cookies = new CookieContainer();
            var steamLoginSecure = Uri.EscapeDataString($"{steamId}||{accessToken}");
            var sessionId = Convert.ToHexString(GenerateRandomBlock(12)).ToLowerInvariant();
            var clientSessionId = Convert.ToHexString(GenerateRandomBlock(8)).ToLowerInvariant();

            foreach (var domain in WebAuthDomains)
            {
                cookies.Add(new Cookie("steamLoginSecure", steamLoginSecure, "/", domain));
                cookies.Add(new Cookie("sessionid", sessionId, "/", domain));
                cookies.Add(new Cookie("clientsessionid", clientSessionId, "/", domain));
            }

            return cookies;
        }

        private static async Task<string> GetRefreshTokenAsync()
        {
            await using var db = await Database.GetConnectionAsync();

            return await db.ExecuteScalarAsync<string>("SELECT `Value` FROM `LocalConfig` WHERE `ConfigKey` = 'backend.loginkey'");
        }

        private static byte[] GenerateRandomBlock(int length)
        {
            var bytes = new byte[length];
            RandomNumberGenerator.Fill(bytes);

            return bytes;
        }

        private static HttpClient CreateWebHttpClient()
        {
            var client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            });

            client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            client.Timeout = TimeSpan.FromSeconds(15);

            return client;
        }

        private static List<KeyValuePair<string, string>> PrepareFormData(HttpMethod method, Uri uri, IEnumerable<KeyValuePair<string, string>> data)
        {
            if (data == null && method == HttpMethod.Get)
            {
                return null;
            }

            var formData = data?.ToList() ?? new List<KeyValuePair<string, string>>();
            var sessionId = Cookies.GetCookies(uri)[SessionIdCookieName]?.Value;

            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                formData.RemoveAll(pair => pair.Key == SessionIdCookieName);
                formData.Add(new KeyValuePair<string, string>(SessionIdCookieName, sessionId));
            }

            return formData.Count > 0 ? formData : null;
        }

        public static async Task<HttpResponseMessage> PerformRequest(HttpMethod method, Uri uri, IEnumerable<KeyValuePair<string, string>> data = null)
        {
            HttpResponseMessage response = null;

            for (var i = 0; i < 3; i++)
            {
                if (!IsAuthorized && !await AuthenticateUser())
                {
                    continue;
                }

                var cookies = string.Empty;

                foreach (var cookie in Cookies.GetCookies(uri))
                {
                    cookies += cookie + ";";
                }

                using var requestMessage = new HttpRequestMessage(method, uri);
                requestMessage.Headers.Add("Cookie", cookies); // Can't pass cookie container into a single req message

                var formData = PrepareFormData(method, uri, data);

                if (formData != null)
                {
                    requestMessage.Content = new FormUrlEncodedContent(formData);
                }

                response = await WebHttpClient.SendAsync(requestMessage);

                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Redirect)
                {
                    Log.WriteDebug(nameof(WebAuth), $"Got status code {response.StatusCode} for {uri}");

                    IsAuthorized = false;
                    Cookies = new CookieContainer();

                    continue;
                }

                response.EnsureSuccessStatusCode();

                break;
            }

            return response;
        }
    }
}

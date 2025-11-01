using Microsoft.Graph;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.Storage.Streams;
using Microsoft.Kiota.Abstractions;
using Azure.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using static TranslatorApp.Pages.WordLookupPage;

namespace TranslatorApp
{
    sealed partial class App : Application
    {
        public static GraphServiceClient? GraphClient { get; private set; }
        public Frame RootFrame { get; private set; }

        private static readonly string CacheFilePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "TranslatorApp", "msal_cache.bin");
        private static readonly object FileLock = new object();

        public App()
        {
            this.InitializeComponent();
            this.Suspending += OnSuspending;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            RootFrame = Window.Current.Content as Frame;

            if (RootFrame == null)
            {
                RootFrame = new Frame();
                Window.Current.Content = RootFrame;
            }

            if (RootFrame.Content == null)
            {
                RootFrame.Navigate(typeof(MainPage), e.Arguments);
            }

            // 异步预加载每日一句
            _ = PreloadDailySentence();

            Window.Current.Activate();
        }

        public async Task SignOutAsync()
        {
            var pca = PublicClientApplicationBuilder
                .Create("e1777b33-099c-4373-bd78-f7ab55d5a2ed")
                .WithRedirectUri("https://login.microsoftonline.com/common/oauth2/nativeclient")
                .Build();

            EnableTokenCacheSerialization(pca.UserTokenCache);

            var accounts = await pca.GetAccountsAsync();
            foreach (var acc in accounts)
                await pca.RemoveAsync(acc);

            if (File.Exists(CacheFilePath))
                File.Delete(CacheFilePath);

            GraphClient = null;
            Debug.WriteLine("[App] 已退出 Microsoft 账号");
        }

        private static void EnableTokenCacheSerialization(ITokenCache tokenCache)
        {
            tokenCache.SetBeforeAccess(args =>
            {
                lock (FileLock)
                {
                    if (File.Exists(CacheFilePath))
                    {
                        args.TokenCache.DeserializeMsalV3(File.ReadAllBytes(CacheFilePath));
                    }
                }
            });

            tokenCache.SetAfterAccess(args =>
            {
                if (args.HasStateChanged)
                {
                    lock (FileLock)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(CacheFilePath)!);
                        File.WriteAllBytes(CacheFilePath, args.TokenCache.SerializeMsalV3());
                    }
                }
            });
        }

        public async Task InitMicrosoftAccountAsync()
        {
            try
            {
                var pca = PublicClientApplicationBuilder
                    .Create("e1777b33-099c-4373-bd78-f7ab55d5a2ed")
                    .WithRedirectUri("https://login.microsoftonline.com/common/oauth2/nativeclient")
                    .Build();

                EnableTokenCacheSerialization(pca.UserTokenCache);

                string[] scopes = { "User.Read", "Files.ReadWrite.AppFolder" };
                AuthenticationResult? result = null;

                var accounts = await pca.GetAccountsAsync();

                try
                {
                    result = await pca.AcquireTokenSilent(scopes, accounts.FirstOrDefault())
                                      .ExecuteAsync();
                    Debug.WriteLine("[App] 静默获取 Token 成功");
                }
                catch (MsalUiRequiredException)
                {
                    try
                    {
                        result = await pca.AcquireTokenInteractive(scopes)
                                          .ExecuteAsync();
                        Debug.WriteLine("[App] 交互式登录成功");
                    }
                    catch (MsalClientException ex)
                    {
                        Debug.WriteLine($"[App] 用户取消登录或登录失败: {ex.Message}");
                        return;
                    }
                }

                if (result == null)
                    return;

                var tokenProvider = new BaseBearerTokenAuthenticationProvider(
                    new DelegateTokenProvider((_, __) => Task.FromResult(result.AccessToken))
                );

                GraphClient = new GraphServiceClient(tokenProvider);

                var me = await GraphClient.Me.GetAsync();
                var photoStream = await GraphClient.Me.Photo.Content.GetAsync();

                BitmapImage? avatarImage = null;
                if (photoStream != null)
                {
                    using var ms = new MemoryStream();
                    await photoStream.CopyToAsync(ms);
                    ms.Position = 0;

                    avatarImage = new BitmapImage();
                    await avatarImage.SetSourceAsync(ms.AsRandomAccessStream());
                }

                // 更新 UI：在 UWP 中可通过事件或直接访问 RootFrame.Content
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[App] 登录流程异常");
                Debug.WriteLine(ex.ToString());
            }
        }

        public DailySentenceData? CachedDailySentence { get; set; }
        public event Action? DailySentenceUpdated;

        private static readonly HttpClient _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true
        });

        public async Task PreloadDailySentence()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                string json = await _http.GetStringAsync("https://open.iciba.com/dsapi/", cts.Token);

                using var doc = JsonDocument.Parse(json);
                string val(string prop) =>
                    doc.RootElement.TryGetProperty(prop, out var el) ? (el.GetString() ?? "") : "";

                var data = new DailySentenceData
                {
                    Caption = val("caption"),
                    Date = val("dateline"),
                    En = val("content"),
                    Zh = val("note"),
                    TtsUrl = val("tts"),
                    PicUrl = val("picture2").Length > 0 ? val("picture2") :
                             val("picture").Length > 0 ? val("picture") : ""
                };

                CachedDailySentence = data;
                DailySentenceUpdated?.Invoke();
            }
            catch
            {
                CachedDailySentence = new DailySentenceData
                {
                    Caption = "",
                    Date = "",
                    En = "每日一句暂不可用",
                    Zh = "",
                    PicUrl = "",
                    TtsUrl = ""
                };
                DailySentenceUpdated?.Invoke();
            }
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            // 保存应用状态
        }

        // 简单 TokenProvider
        private class DelegateTokenProvider : IAccessTokenProvider
        {
            private readonly Func<TokenRequestContext, CancellationToken, Task<string>> _acquireToken;

            public DelegateTokenProvider(Func<TokenRequestContext, CancellationToken, Task<string>> acquireToken)
            {
                _acquireToken = acquireToken;
            }

            public AllowedHostsValidator AllowedHostsValidator { get; } = new AllowedHostsValidator();

            public Task<string> GetAuthorizationTokenAsync(
                Uri uri,
                Dictionary<string, object>? additionalAuthenticationContext = null,
                CancellationToken cancellationToken = default)
            {
                return _acquireToken(new TokenRequestContext(), cancellationToken);
            }
        }
    }
}
using Azure.Core;
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
using Windows.ApplicationModel.Core;
using Windows.Security.Authentication.Web;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using WinRT.Interop;
using muxc = Microsoft.UI.Xaml.Controls;

namespace TranslatorApp
{
    public partial class App : Application
    {
        public static GraphServiceClient? GraphClient { get; private set; }
        private Windows.UI.ViewManagement.UISettings _uiSettings;
        private static readonly string CacheFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TranslatorApp", "msal_cache.bin");
        private static readonly object FileLock = new object();
        public TranslatorApp.Pages.WordLookupPage.DailySentenceData? CachedDailySentence { get; set; }
        public event Action? DailySentenceUpdated;
        private static readonly HttpClient _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        private const string HideCssRules = @"header, .top, .top-nav, .top-nav-wrap, .nav, .nav-bar, .nav-wrap, .top-banner,
            .header, .header-light,
            .search-wrapper, .search-area, .search-bar-container, .search-bar-bg,
            header .search, header .search-wrapper, header .search-box,
            .search-box, .search-container, .search-bar-wrap,
            footer, .footer, .yd-footer, .global-footer, .ft, .ft-wrap, .ft-container,
            .footer-light, .light-footer, .m-ft, .m-footer, .footer-wrap,
            [class*='footer'], [id*='footer'], [class*='copyright'], [id*='copyright'] {
                display: none !important;
            }";
        private const string BingHideCss = @"#b_header, #sw_hdr, .b_scopebar, .b_logo { display: none !important; }
#sb_form { position:absolute !important;left:-9999px!important;top:auto!important;width:1px!important;height:1px!important;overflow:hidden!important;opacity:0!important;pointer-events:none!important; }
#b_footer, .b_footnote, #b_pageFeedback, #b_feedback,[role='contentinfo'], footer { display: none !important; }
.b_pag, nav[aria-label*='Pagination'], nav[role='navigation'][aria-label*='页'] { display: block !important; visibility: visible !important; }";
        private const string YoudaoDarkCss = @":root{ color-scheme: dark; } html, body{ background:#0f0f0f !important; color:#ddd !important; } a{ color:#6fb1ff !important; }";
        private static bool isClosing = false;

        // OAuth 应用配置（请确保 Azure 应用注册中已添加重定向 URI：myapp://auth）
        private const string OAuthClientId = "63de19bb-351b-40e8-8a37-cb657eb6e685";
        private const string OAuthRedirectUri = "myapp://auth";

        // 注意：持久化登录需要 offline_access
        private const string OAuthScopes = "openid profile offline_access User.Read Files.ReadWrite.AppFolder";

        // PasswordVault 存储标识
        private const string VaultResource = "TranslatorApp.MSAL";
        private const string VaultUser = "DefaultUser";

        // 运行时令牌状态（内存缓存）
        private static string? _accessToken;
        private static DateTimeOffset _accessTokenExpires = DateTimeOffset.MinValue;
        private static string? _refreshToken;

        public App()
        {
            InitializeComponent();
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        }

        public async Task SignOutAsync()
        {
            try
            {
                // 清空 GraphClient
                GraphClient = null;

                // 清空内存状态
                _accessToken = null;
                _accessTokenExpires = DateTimeOffset.MinValue;
                _refreshToken = null;

                // 清除 PasswordVault 中的 refresh_token
                ClearRefreshToken();

                // 如果你仍然保留了旧的 msal_cache.bin 文件，这里一并删除
                try
                {
                    if (File.Exists(CacheFilePath))
                        File.Delete(CacheFilePath);
                }
                catch { }

                Debug.WriteLine("[App] 已退出 Microsoft 账号");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[App] 退出流程异常: " + ex);
            }

            await Task.CompletedTask;
        }
        // Graph 调用前确保有最新的 access_token
        private async Task<string> EnsureAccessTokenAsync()
        {
            // 若 access_token 仍有效（提前 2 分钟），直接返回
            if (!string.IsNullOrWhiteSpace(_accessToken) &&
                DateTimeOffset.UtcNow < _accessTokenExpires - TimeSpan.FromMinutes(2))
            {
                return _accessToken!;
            }

            // 尝试用 refresh_token 续期
            var rt = _refreshToken ?? LoadRefreshToken();
            if (!string.IsNullOrWhiteSpace(rt))
            {
                var token = await RefreshAccessTokenAsync(rt!);
                if (!string.IsNullOrWhiteSpace(token.AccessToken))
                {
                    _accessToken = token.AccessToken;
                    _accessTokenExpires = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
                    if (!string.IsNullOrWhiteSpace(token.RefreshToken))
                    {
                        _refreshToken = token.RefreshToken;
                        SaveRefreshToken(token.RefreshToken);
                    }
                    return _accessToken!;
                }
            }

            // 没有可用令牌，抛出让上层触发交互登录
            throw new UnauthorizedAccessException("Access token 不可用，需要交互登录。");
        }
        private sealed class TokenResponse
        {
            public string AccessToken { get; set; } = "";
            public string? RefreshToken { get; set; }
            public int ExpiresIn { get; set; } = 3600;
            public string TokenType { get; set; } = "Bearer";
        }

        private async Task<TokenResponse> ExchangeCodeForTokenAsync(string code, string codeVerifier)
        {
            using var http = new HttpClient();
            var form = new Dictionary<string, string>
    {
        { "client_id", OAuthClientId },
        { "redirect_uri", OAuthRedirectUri },
        { "grant_type", "authorization_code" },
        { "code", code },
        { "code_verifier", codeVerifier },
        { "scope", OAuthScopes }
    };

            var resp = await http.PostAsync(
                "https://login.microsoftonline.com/common/oauth2/v2.0/token",
                new FormUrlEncodedContent(form));

            var json = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var token = new TokenResponse
            {
                AccessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "",
                RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                ExpiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600,
                TokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() ?? "Bearer" : "Bearer"
            };

            if (string.IsNullOrWhiteSpace(token.AccessToken))
                throw new InvalidOperationException("未能获取 Access Token：" + json);

            return token;
        }

        private async Task<TokenResponse> RefreshAccessTokenAsync(string refreshToken)
        {
            using var http = new HttpClient();
            var form = new Dictionary<string, string>
    {
        { "client_id", OAuthClientId },
        { "redirect_uri", OAuthRedirectUri },
        { "grant_type", "refresh_token" },
        { "refresh_token", refreshToken },
        { "scope", OAuthScopes }
    };

            var resp = await http.PostAsync(
                "https://login.microsoftonline.com/common/oauth2/v2.0/token",
                new FormUrlEncodedContent(form));

            var json = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var token = new TokenResponse
            {
                AccessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "",
                RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                ExpiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600,
                TokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() ?? "Bearer" : "Bearer"
            };

            if (string.IsNullOrWhiteSpace(token.AccessToken))
                throw new InvalidOperationException("刷新 Access Token 失败：" + json);

            return token;
        }
        private static (string Verifier, string Challenge) GeneratePkce()
        {
            // 生成高强度随机 verifier（43-128 字节 Base64Url）
            using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            var bytes = new byte[64];
            rng.GetBytes(bytes);
            var verifier = Base64UrlEncode(bytes);

            // challenge = BASE64URL(SHA256(verifier))
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.ASCII.GetBytes(verifier));
            var challenge = Base64UrlEncode(hash);

            return (verifier, challenge);
        }

        private static string Base64UrlEncode(byte[] input)
        {
            var s = Convert.ToBase64String(input)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
            return s;
        }
        private static void SaveRefreshToken(string? refreshToken)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(refreshToken)) return;
                var vault = new Windows.Security.Credentials.PasswordVault();

                // 先删除旧的
                try
                {
                    var existing = vault.FindAllByResource(VaultResource);
                    foreach (var item in existing)
                    {
                        if (item.UserName == VaultUser)
                        {
                            vault.Remove(item);
                        }
                    }
                }
                catch { /* 没有旧项也正常 */ }

                vault.Add(new Windows.Security.Credentials.PasswordCredential(VaultResource, VaultUser, refreshToken));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[App] 保存 refresh_token 失败: " + ex);
            }
        }

        private static string? LoadRefreshToken()
        {
            try
            {
                var vault = new Windows.Security.Credentials.PasswordVault();
                var list = vault.FindAllByResource(VaultResource);
                var item = list.FirstOrDefault(i => i.UserName == VaultUser);
                if (item != null)
                {
                    var cred = vault.Retrieve(VaultResource, VaultUser);
                    return cred.Password;
                }
            }
            catch { }
            return null;
        }

        private static void ClearRefreshToken()
        {
            try
            {
                var vault = new Windows.Security.Credentials.PasswordVault();
                var list = vault.FindAllByResource(VaultResource);
                foreach (var item in list)
                {
                    if (item.UserName == VaultUser)
                        vault.Remove(item);
                }
            }
            catch { }
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
                // 生成 PKCE 参数
                var (codeVerifier, codeChallenge) = GeneratePkce();

                // 构造授权 URL（带 PKCE）
                var authorizeUrl =
                    $"https://login.microsoftonline.com/common/oauth2/v2.0/authorize" +
                    $"?client_id={OAuthClientId}" +
                    $"&response_type=code" +
                    $"&redirect_uri={Uri.EscapeDataString(OAuthRedirectUri)}" +
                    $"&response_mode=query" +
                    $"&scope={Uri.EscapeDataString(OAuthScopes)}" +
                    $"&code_challenge={codeChallenge}" +
                    $"&code_challenge_method=S256";

                // 打开系统账户选择器
                var result = await WebAuthenticationBroker.AuthenticateAsync(
                    WebAuthenticationOptions.None,
                    new Uri(authorizeUrl),
                    new Uri(OAuthRedirectUri));

                if (result.ResponseStatus == WebAuthenticationStatus.Success)
                {
                    // 解析授权码
                    var responseUri = new Uri(result.ResponseData);
                    var queryParams = System.Web.HttpUtility.ParseQueryString(responseUri.Query);
                    var code = queryParams["code"];

                    if (!string.IsNullOrEmpty(code))
                    {
                        // 用授权码 + code_verifier 换取令牌
                        var token = await ExchangeCodeForTokenAsync(code, codeVerifier);

                        if (!string.IsNullOrWhiteSpace(token.AccessToken))
                        {
                            // 保存 refresh_token（持久化）
                            SaveRefreshToken(token.RefreshToken);

                            // 更新内存状态
                            _accessToken = token.AccessToken;
                            _accessTokenExpires = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
                            _refreshToken = token.RefreshToken;

                            // 初始化 GraphClient（动态令牌提供器）
                            GraphClient = new GraphServiceClient(new BaseBearerTokenAuthenticationProvider(
                                new DelegateTokenProvider(async (_, __) => await EnsureAccessTokenAsync())));

                            // 获取用户信息与头像
                            if (GraphClient != null)
                            {
                                try
                                {
                                    // 获取用户基本信息（包含 displayName, mail, userPrincipalName）
                                    var me = await GraphClient.Me.GetAsync();

                                    string displayName = me?.DisplayName ?? me?.UserPrincipalName ?? string.Empty;
                                    string email = me?.Mail ?? me?.UserPrincipalName ?? string.Empty;

                                    // 尝试获取头像流
                                    System.IO.Stream? photoStream = null;
                                    try
                                    {
                                        photoStream = await GraphClient.Me.Photo.Content.GetAsync();
                                    }
                                    catch
                                    {
                                        photoStream = null;
                                    }

                                    BitmapImage? avatarImage = null;
                                    if (photoStream is not null)
                                    {
                                        using var ms = new MemoryStream();
                                        await photoStream.CopyToAsync(ms);
                                        ms.Position = 0;
                                        avatarImage = new BitmapImage();
                                        await avatarImage.SetSourceAsync(ms.AsRandomAccessStream());
                                    }

                                    // 在 UI 线程更新 MainPage 并刷新标题栏
                                    await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(
                                        Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                                        {
                                            try
                                            {
                                                MainPage.Current?.UpdateAccountUI(displayName, avatarImage, email);
                                            }
                                            catch (Exception uiEx)
                                            {
                                                Debug.WriteLine("[InitMicrosoftAccountAsync] 更新 UI 异常: " + uiEx);
                                            }
                                        });
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine("[InitMicrosoftAccountAsync] 获取用户信息/头像失败: " + ex);
                                }
                            }
                        }
                        else

                        {
                            Debug.WriteLine("[InitMicrosoftAccountAsync] 未获取到 access token");
                        }
                    }
                    else
                    {
                        Debug.WriteLine("[InitMicrosoftAccountAsync] 授权码为空");
                    }
                }
                else if (result.ResponseStatus == WebAuthenticationStatus.ErrorHttp)
                {
                    Debug.WriteLine("[InitMicrosoftAccountAsync] 登录失败，HTTP 错误: " + result.ResponseErrorDetail);
                }
                else
                {
                    Debug.WriteLine("[InitMicrosoftAccountAsync] 登录取消或失败: " + result.ResponseStatus);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[InitMicrosoftAccountAsync] 登录流程异常: " + ex);
            }
        }

        public async Task TryRestoreLoginAsync()
        {
            try
            {
                // 从 PasswordVault 恢复 refresh_token
                _refreshToken = LoadRefreshToken();
                if (string.IsNullOrWhiteSpace(_refreshToken))
                {
                    Debug.WriteLine("[App] 没有可用的 refresh_token，需要交互登录。");
                    return;
                }

                // 用 refresh_token 静默续期
                var token = await RefreshAccessTokenAsync(_refreshToken);
                if (!string.IsNullOrWhiteSpace(token.AccessToken))
                {
                    _accessToken = token.AccessToken;
                    _accessTokenExpires = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
                    _refreshToken = token.RefreshToken ?? _refreshToken; // 有些返回不会更新 refresh_token

                    // 刷新持久化（若返回了新的 refresh_token）
                    if (!string.IsNullOrWhiteSpace(token.RefreshToken) && token.RefreshToken != LoadRefreshToken())
                    {
                        SaveRefreshToken(token.RefreshToken);
                    }

                    // 初始化 GraphClient（动态令牌提供器）
                    GraphClient = new GraphServiceClient(new BaseBearerTokenAuthenticationProvider(
                        new DelegateTokenProvider(async (_, __) => await EnsureAccessTokenAsync())));

                    await FetchAndUpdateAccountUiAsync();
                }
                else
                {
                    Debug.WriteLine("[App] 静默续期失败，需要交互登录。");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[App] 恢复登录失败: " + ex);
            }
        }

        // 获取当前 Graph 会话的用户信息与头像，并更新 UI
        private async Task FetchAndUpdateAccountUiAsync()
        {
            if (GraphClient == null) return;

            try
            {
                var me = await GraphClient.Me.GetAsync();

                System.IO.Stream? photoStream = null;
                try
                {
                    photoStream = await GraphClient.Me.Photo.Content.GetAsync();
                }
                catch
                {
                    photoStream = null;
                }

                BitmapImage? avatarImage = null;
                if (photoStream is not null)
                {
                    using var ms = new MemoryStream();
                    await photoStream.CopyToAsync(ms);
                    ms.Position = 0;
                    avatarImage = new BitmapImage();
                    await avatarImage.SetSourceAsync(ms.AsRandomAccessStream());
                }

                try
                {
                    if (MainPage.Current != null)
                    {
                        MainPage.Current.UpdateAccountUI(me?.DisplayName ?? "", avatarImage);
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[App] 获取用户信息/头像失败: " + ex);
            }
        }

        public static void ClearGraphSession() => GraphClient = null;

        private class DelegateTokenProvider : IAccessTokenProvider
        {
            private readonly Func<TokenRequestContext, CancellationToken, Task<string>> _acquireToken;
            public DelegateTokenProvider(Func<TokenRequestContext, CancellationToken, Task<string>> acquireToken) => _acquireToken = acquireToken;
            public AllowedHostsValidator AllowedHostsValidator { get; } = new AllowedHostsValidator();
            public Task<string> GetAuthorizationTokenAsync(Uri uri, Dictionary<string, object>? additionalAuthenticationContext = null, CancellationToken cancellationToken = default)
                => _acquireToken(new TokenRequestContext(), cancellationToken);
        }

        public async Task PreloadDailySentence()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                string json = await _http.GetStringAsync("https://open.iciba.com/dsapi/", cts.Token);
                using var doc = JsonDocument.Parse(json);
                string val(string prop) => doc.RootElement.TryGetProperty(prop, out var el) ? (el.GetString() ?? "") : "";
                var data = new TranslatorApp.Pages.WordLookupPage.DailySentenceData
                {
                    Caption = val("caption"),
                    Date = val("dateline"),
                    En = val("content"),
                    Zh = val("note"),
                    TtsUrl = val("tts"),
                    PicUrl = val("picture2").Length > 0 ? val("picture2") : (val("picture").Length > 0 ? val("picture") : "")
                };
                if (!string.IsNullOrWhiteSpace(data.PicUrl) && Uri.IsWellFormedUriString(data.PicUrl, UriKind.Absolute))
                {
                    try
                    {
                        using var ctsImg = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        byte[] bytes = await _http.GetByteArrayAsync(data.PicUrl, ctsImg.Token);
                        if (bytes.Length > 0)
                        {
                            var ext = data.PicUrl.ToLowerInvariant().EndsWith(".png") ? "image/png" : data.PicUrl.ToLowerInvariant().EndsWith(".webp") ? "image/webp" : "image/jpeg";
                            data.PicUrl = $"data:{ext};base64,{Convert.ToBase64String(bytes)}";
                        }
                    }
                    catch { }
                }
                CachedDailySentence = data;
                DailySentenceUpdated?.Invoke();
            }
            catch
            {
                CachedDailySentence = new TranslatorApp.Pages.WordLookupPage.DailySentenceData { Caption = "", Date = "", En = "每日一句暂不可用", Zh = "", PicUrl = "", TtsUrl = "" };
                DailySentenceUpdated?.Invoke();
            }
        }

        protected override void OnLaunched(Windows.ApplicationModel.Activation.LaunchActivatedEventArgs args)
        {
            _uiSettings = new Windows.UI.ViewManagement.UISettings();
            _uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;

            try
            {
                // 首选最小尺寸（DIP 单位）
                try
                {
                    Windows.UI.ViewManagement.ApplicationView.GetForCurrentView()
                        .SetPreferredMinSize(new Windows.Foundation.Size(900, 680));
                }
                catch (Exception exSize)
                {
                    Debug.WriteLine("[OnLaunched] Set preferred size failed: " + exSize);
                }

                var rootFrame = Window.Current.Content as Frame ?? new Frame();

                if (Window.Current.Content == null)
                    Window.Current.Content = rootFrame;

                if (rootFrame.Content == null)
                {
                    bool navigated = rootFrame.Navigate(typeof(MainPage), args?.Arguments);
                    Debug.WriteLine($"Navigate to MainPage result: {navigated}");
                }

                // 激活窗口（保证 UI 已就绪）
                Window.Current.Activate();

                // 立即应用主题与背景（不要延迟），并启动静默登录与其他后台初始化
                try
                {
                    ApplySavedTheme();
                    ApplySavedBackdrop();
                }
                catch (Exception exTheme)
                {
                    Debug.WriteLine("[OnLaunched] Apply theme/backdrop failed: " + exTheme);
                }

                // 启动静默恢复登录和可选预加载，但不阻塞启动流程
                _ = TryRestoreLoginAsync();
                // 可选预加载每日一句（异步、不阻塞）
                _ = PreloadDailySentence();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[App OnLaunched] " + ex);
            }
        }

        private void UiSettings_ColorValuesChanged(Windows.UI.ViewManagement.UISettings sender, object args)
        {
            var _ = Window.Current.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
            {
                await Task.Delay(50);
                try
                {
                    var tag = ApplicationData.Current.LocalSettings.Values["BackdropMaterial"] as string ?? "Mica";
                    var themeValue = ApplicationData.Current.LocalSettings.Values["AppTheme"] as string ?? "Default";
                    var requested = themeValue switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
                    ApplyThemeAndBackdrop(tag, requested);
                }
                catch { }
            });
        }

        private void ApplySavedTheme()
        {
            var themeValue = ApplicationData.Current.LocalSettings.Values["AppTheme"] as string ?? "Default";
            var theme = themeValue switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
            if (Window.Current.Content is FrameworkElement fe) fe.RequestedTheme = theme;
        }

        private void ApplySavedBackdrop()
        {
            var tag = ApplicationData.Current.LocalSettings.Values["BackdropMaterial"] as string ?? "Mica";
            var themeValue = ApplicationData.Current.LocalSettings.Values["AppTheme"] as string ?? "Default";
            var requested = themeValue switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
            ApplyThemeAndBackdrop(tag, requested);
        }

        private Color GetThemeAwareColor()
        {
            var fe = Window.Current.Content as FrameworkElement;
            var theme = fe?.RequestedTheme ?? ElementTheme.Default;
            return (theme == ElementTheme.Dark) ? Colors.Black : Colors.White;
        }

        public static void StopWebView2Intercept() => isClosing = true;

        public static async Task InitWebView2Async(Microsoft.UI.Xaml.Controls.WebView2 webView, string? initialQuery = null, bool followSystemTheme = true, ElementTheme fixedTheme = ElementTheme.Default)
        {
            if (webView == null) return;
            await webView.EnsureCoreWebView2Async();
            var core = webView.CoreWebView2;
            Func<bool> isDarkNow = () => followSystemTheme ? (webView.ActualTheme == ElementTheme.Dark) : (fixedTheme == ElementTheme.Dark);

            core.NavigationStarting += (s, e) =>
            {
                try
                {
                    var uri = e.Uri ?? "";
                    if (uri.Contains("bing.com/dict", StringComparison.OrdinalIgnoreCase) && uri.Contains("q=welcome", StringComparison.OrdinalIgnoreCase))
                    {
                        e.Cancel = true;
                        var target = !string.IsNullOrWhiteSpace(initialQuery) ? $"https://cn.bing.com/dict/search?q={Uri.EscapeDataString(initialQuery)}" : "https://cn.bing.com/dict/";
                        core.Navigate(target);
                        return;
                    }
                    var defaultUA = core.Settings.UserAgent;
                    if (!defaultUA.Contains("TranslatorApp")) core.Settings.UserAgent = defaultUA + " TranslatorApp/1.0";
                    core.Profile.PreferredColorScheme = isDarkNow() ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
                }
                catch { }
            };

            if (followSystemTheme)
            {
                webView.ActualThemeChanged += async (_, __) =>
                {
                    try
                    {
                        bool dark = isDarkNow();
                        core.Profile.PreferredColorScheme = dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
                        var expires = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
                        foreach (var domain in new[] { ".cn.bing.com", ".bing.com" })
                        {
                            var srch = core.CookieManager.CreateCookie("SRCHHPGUSR", dark ? "DARK=2" : "DARK=0", domain, "/");
                            srch.Expires = expires;
                            core.CookieManager.AddOrUpdateCookie(srch);
                            var dict = core.CookieManager.CreateCookie("DICTTHEME", "system", domain, "/dict/");
                            dict.Expires = expires;
                            core.CookieManager.AddOrUpdateCookie(dict);
                        }
                        var t = dark ? "dark" : "light";
                        await core.ExecuteScriptAsync($@"
(() => {{
  const t = '{t}';
  try {{
    localStorage.setItem('dict_theme', t);
    localStorage.setItem('themeSetting', t);
    localStorage.setItem('theme', t);
    const s = JSON.parse(localStorage.getItem('settings') || '{{}}');
    s.theme = t; localStorage.setItem('settings', JSON.stringify(s));
  }} catch(e) {{
    localStorage.setItem('settings', JSON.stringify({{ theme: t }}));
  }}
  document.documentElement.setAttribute('data-theme', t);
  document.body && document.body.setAttribute('data-theme', t);
  if (window.setTheme) try{{window.setTheme(t);}}catch(_){{}}
}})();");
                    }
                    catch { }
                };
            }

            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Document);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Stylesheet);

            core.WebResourceRequested += async (s, e) =>
            {
                if (isClosing || core?.Environment == null || e.ResourceContext != CoreWebView2WebResourceContext.Document) return;
                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || !uri.Host.EndsWith("youdao.com", StringComparison.OrdinalIgnoreCase)) return;
                var deferral = e.GetDeferral();
                try
                {
                    string html;
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        html = await _http.GetStringAsync(e.Request.Uri, cts.Token);
                    }
                    catch { deferral.Complete(); return; }
                    int headIndex = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
                    if (headIndex >= 0)
                    {
                        int closeHeadTag = html.IndexOf(">", headIndex);
                        if (closeHeadTag > 0)
                        {
                            bool isDark = Window.Current.Content is FrameworkElement fe && fe.ActualTheme == ElementTheme.Dark;
                            string extraDarkCss = isDark ? YoudaoDarkCss : "";
                            string styleTag = $"<style>{HideCssRules}{extraDarkCss}[id*='feedback'],[class*='feedback']{{display:none!important;pointer-events:none!important;}}</style>";
                            html = html.Insert(closeHeadTag + 1, styleTag);
                        }
                    }
                    var ras = new InMemoryRandomAccessStream();
                    var writer = new DataWriter(ras) { UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8, ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian };
                    writer.WriteBytes(Encoding.UTF8.GetBytes(html));
                    await writer.StoreAsync();
                    ras.Seek(0);
                    e.Response = core.Environment.CreateWebResourceResponse(ras, 200, "OK", "Content-Type: text/html; charset=utf-8");
                }
                finally { deferral.Complete(); }
            };

            core.WebResourceRequested += async (s, e) =>
            {
                if (isClosing || core?.Environment == null || e.ResourceContext != CoreWebView2WebResourceContext.Stylesheet) return;
                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || !uri.Host.EndsWith("youdao.com", StringComparison.OrdinalIgnoreCase)) return;
                var deferral = e.GetDeferral();
                try
                {
                    string cssText;
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        cssText = await _http.GetStringAsync(e.Request.Uri, cts.Token);
                    }
                    catch { deferral.Complete(); return; }
                    bool isDark = Window.Current.Content is FrameworkElement fe && fe.ActualTheme == ElementTheme.Dark;
                    if (isDark) cssText += "\n" + YoudaoDarkCss;
                    cssText += "\n" + HideCssRules + "\n[id*='feedback'],[class*='feedback']{display:none!important;pointer-events:none!important;}";
                    var ras = new InMemoryRandomAccessStream();
                    var writer = new DataWriter(ras) { UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8, ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian };
                    writer.WriteBytes(Encoding.UTF8.GetBytes(cssText));
                    await writer.StoreAsync();
                    ras.Seek(0);
                    e.Response = core.Environment.CreateWebResourceResponse(ras, 200, "OK", "Content-Type: text/css; charset=utf-8");
                }
                finally { deferral.Complete(); }
            };

            core.WebResourceRequested += async (s, e) =>
            {
                if (isClosing || core?.Environment == null || e.ResourceContext != CoreWebView2WebResourceContext.Document) return;
                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || !uri.Host.EndsWith("bing.com", StringComparison.OrdinalIgnoreCase)) return;
                var deferral = e.GetDeferral();
                try
                {
                    string html;
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        html = await _http.GetStringAsync(e.Request.Uri, cts.Token);
                    }
                    catch { deferral.Complete(); return; }
                    int headIndex = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
                    if (headIndex >= 0)
                    {
                        int closeHeadTag = html.IndexOf(">", headIndex);
                        if (closeHeadTag > 0)
                        {
                            string safeBingCss = BingHideCss.Replace("#sb_form,", @"#sb_form { position:absolute !important;left:-9999px!important;top:auto!important;width:1px!important;height:1px!important;overflow:hidden!important;opacity:0!important;pointer-events:none!important; }");
                            string styleTag = $"<style>{safeBingCss}</style>";
                            bool isDark = Window.Current.Content is FrameworkElement fe && fe.ActualTheme == ElementTheme.Dark;
                            styleTag += isDark ? "<meta name=\"color-scheme\" content=\"dark\">" : "<meta name=\"color-scheme\" content=\"light\">";
                            html = html.Insert(closeHeadTag + 1, styleTag);
                        }
                    }
                    var ras = new InMemoryRandomAccessStream();
                    var writer = new DataWriter(ras) { UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8, ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian };
                    writer.WriteBytes(Encoding.UTF8.GetBytes(html));
                    await writer.StoreAsync();
                    ras.Seek(0);
                    e.Response = core.Environment.CreateWebResourceResponse(ras, 200, "OK", "Content-Type: text/html; charset=utf-8");
                }
                finally { deferral.Complete(); }
            };

            core.WebResourceRequested += async (s, e) =>
            {
                if (isClosing || core?.Environment == null || e.ResourceContext != CoreWebView2WebResourceContext.Stylesheet) return;
                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || !uri.Host.EndsWith("bing.com", StringComparison.OrdinalIgnoreCase)) return;
                var deferral = e.GetDeferral();
                try
                {
                    string cssText;
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        cssText = await _http.GetStringAsync(e.Request.Uri, cts.Token);
                    }
                    catch { deferral.Complete(); return; }
                    cssText += "\n" + BingHideCss;
                    var ras = new InMemoryRandomAccessStream();
                    var writer = new DataWriter(ras) { UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8, ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian };
                    writer.WriteBytes(Encoding.UTF8.GetBytes(cssText));
                    await writer.StoreAsync();
                    ras.Seek(0);
                    e.Response = core.Environment.CreateWebResourceResponse(ras, 200, "OK", "Content-Type: text/css; charset=utf-8");
                }
                finally { deferral.Complete(); }
            };

            core.NavigationStarting += (s, e) =>
            {
                try
                {
                    var defaultUA = core.Settings.UserAgent;
                    if (!defaultUA.Contains("TranslatorApp")) core.Settings.UserAgent = defaultUA + " TranslatorApp/1.0";
                    bool isDarkTheme = Window.Current.Content is FrameworkElement fe2 && fe2.ActualTheme == ElementTheme.Dark;
                    core.Profile.PreferredColorScheme = isDarkTheme ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
                }
                catch { }
            };

            await Task.CompletedTask;
            string themeValue = (followSystemTheme ? webView.ActualTheme == ElementTheme.Dark : fixedTheme == ElementTheme.Dark) ? "dark" : "light";
            await core.AddScriptToExecuteOnDocumentCreatedAsync($@"
    localStorage.setItem('BingDictTheme','{themeValue}');
    document.cookie='BingDictTheme={themeValue};path=/;domain=.bing.com';
");
            if (!string.IsNullOrEmpty(initialQuery)) core.Navigate($"https://cn.bing.com/dict/search?q={Uri.EscapeDataString(initialQuery)}");
        }

        public static bool IsSystemInDarkMode()
        {
            try
            {
                var ui = new Windows.UI.ViewManagement.UISettings();
                var bg = ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
                double luminance = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255.0;
                return luminance < 0.5;
            }
            catch { return false; }
        }

        public void ApplyThemeAndBackdrop(string tag, ElementTheme requestedTheme)
        {
            bool useDark = requestedTheme == ElementTheme.Default ? IsSystemInDarkMode() : (requestedTheme == ElementTheme.Dark);
            try
            {
                var titleBar = ApplicationView.GetForCurrentView().TitleBar;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                titleBar.ButtonHoverBackgroundColor = Colors.Transparent;
                titleBar.ButtonPressedBackgroundColor = Colors.Transparent;
                var fg = useDark ? Colors.White : Colors.Black;
                var fgInactive = useDark ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0x00, 0x00, 0x00);
                titleBar.ButtonForegroundColor = fg;
                titleBar.ButtonInactiveForegroundColor = fgInactive;
                titleBar.ButtonHoverForegroundColor = fg;
                titleBar.ButtonPressedForegroundColor = fg;
            }
            catch { }

            var root = Window.Current.Content as Control;
            if (root == null) return;

            try
            {
                switch (tag)
                {
                    case "None":
                        muxc.BackdropMaterial.SetApplyToRootOrPageBackground(root, false);
                        root.Background = new SolidColorBrush(useDark ? Colors.Black : Colors.White);
                        break;
                    case "Acrylic":
                        muxc.BackdropMaterial.SetApplyToRootOrPageBackground(root, false);
                        var tint = useDark ? Colors.Black : Colors.White;
                        var acrylic = new Windows.UI.Xaml.Media.AcrylicBrush
                        {
                            BackgroundSource = Windows.UI.Xaml.Media.AcrylicBackgroundSource.HostBackdrop,
                            TintColor = tint,
                            TintOpacity = 0.6,
                            FallbackColor = tint
                        };
                        root.Background = acrylic;
                        break;
                    case "Mica":
                    default:
                        muxc.BackdropMaterial.SetApplyToRootOrPageBackground(root, true);
                        root.ClearValue(Control.BackgroundProperty);
                        break;
                }
            }
            catch
            {
                muxc.BackdropMaterial.SetApplyToRootOrPageBackground(root, false);
                root.Background = new SolidColorBrush(useDark ? Colors.Black : Colors.White);
            }
        }
    }
}
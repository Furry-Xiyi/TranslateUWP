using Azure.Core;
using Microsoft.Graph;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.UI.Xaml;
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
using TranslatorApp.Pages;
using Windows.ApplicationModel.Core;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using muxc = Microsoft.UI.Xaml.Controls; // WinUI2 命名空间别名

namespace TranslatorApp
{
    public partial class App : Application
    {
        public static GraphServiceClient? GraphClient { get; private set; }
        private Windows.UI.ViewManagement.UISettings _uiSettings;

        private static readonly string CacheFilePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "TranslatorApp", "msal_cache.bin");
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

        // 标题栏拖拽区（由 App 统一维护）
        private UIElement? _dragElement;
        private FrameworkElement? _customRegion;
        private FrameworkElement? _accountElement;
        private FrameworkElement? _centerHost;

        private CoreApplicationViewTitleBar? _coreTitleBar;
        private bool _titlebarRegistered = false;

        private double _leftInset = 0;  // 逻辑像素
        private double _rightInset = 0; // 逻辑像素
        private double _rawPixelsPerViewPixel = 1.0;

        public App()
        {
            InitializeComponent();
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        }

        // ---------- MSAL / Token cache ----------
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
                    result = await pca.AcquireTokenSilent(scopes, accounts.FirstOrDefault()).ExecuteAsync();
                    Debug.WriteLine("[App] 静默获取 Token 成功");
                }
                catch (MsalUiRequiredException)
                {
                    try
                    {
                        result = await pca.AcquireTokenInteractive(scopes).ExecuteAsync();
                        Debug.WriteLine("[App] 交互式登录成功");
                    }
                    catch (MsalClientException ex)
                    {
                        Debug.WriteLine($"[App] 用户取消登录或登录失败: {ex.Message}");
                        return;
                    }
                }

                if (result == null) return;

                var tokenProvider = new BaseBearerTokenAuthenticationProvider(
                    new DelegateTokenProvider((_, __) => Task.FromResult(result.AccessToken))
                );

                GraphClient = new GraphServiceClient(tokenProvider);

                var me = await GraphClient.Me.GetAsync();
                System.IO.Stream? photoStream = null;
                try { photoStream = await GraphClient!.Me.Photo.Content.GetAsync(); } catch { photoStream = null; }

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
                        RefreshTitleBarNow();
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[App] 登录流程异常: " + ex);
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

        // ---------- 每日一句 ----------
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
                            var ext = data.PicUrl.ToLowerInvariant().EndsWith(".png") ? "image/png" :
                                      data.PicUrl.ToLowerInvariant().EndsWith(".webp") ? "image/webp" : "image/jpeg";
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
                CachedDailySentence = new TranslatorApp.Pages.WordLookupPage.DailySentenceData
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

        // ---------- 启动 ----------
        protected override void OnLaunched(Windows.ApplicationModel.Activation.LaunchActivatedEventArgs args)
        {
            _uiSettings = new Windows.UI.ViewManagement.UISettings();
            _uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;

            try
            {
                var rootFrame = Window.Current.Content as Frame ?? new Frame();
                if (Window.Current.Content == null) Window.Current.Content = rootFrame;

                if (rootFrame.Content == null)
                {
                    bool navigated = rootFrame.Navigate(typeof(MainPage), args?.Arguments);
                    Debug.WriteLine($"Navigate to MainPage result: {navigated}");
                }

                ApplySavedTheme();
                ApplySavedBackdrop();

                Window.Current.Activate();

                // 启动后一帧再应用，避免时序问题
                var _ = Window.Current.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
                {
                    await Task.Delay(50);
                    ApplySavedTheme();
                    ApplySavedBackdrop();
                });
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
                    var requested = themeValue switch
                    {
                        "Light" => ElementTheme.Light,
                        "Dark" => ElementTheme.Dark,
                        _ => ElementTheme.Default
                    };
                    ApplyThemeAndBackdrop(tag, requested);
                }
                catch { }
            });
        }

        private void ApplySavedTheme()
        {
            var themeValue = ApplicationData.Current.LocalSettings.Values["AppTheme"] as string ?? "Default";
            var theme = themeValue switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
            if (Window.Current.Content is FrameworkElement fe) fe.RequestedTheme = theme;
        }

        private void ApplySavedBackdrop()
        {
            var tag = ApplicationData.Current.LocalSettings.Values["BackdropMaterial"] as string ?? "Mica";
            var themeValue = ApplicationData.Current.LocalSettings.Values["AppTheme"] as string ?? "Default";
            var requested = themeValue switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
            ApplyThemeAndBackdrop(tag, requested);
        }

        private Color GetThemeAwareColor()
        {
            var fe = Window.Current.Content as FrameworkElement;
            var theme = fe?.RequestedTheme ?? ElementTheme.Default;
            return (theme == ElementTheme.Dark) ? Colors.Black : Colors.White;
        }

        // ---------- WebView2 初始化与注入 ----------
        public static void StopWebView2Intercept() => isClosing = true;

        public static async Task InitWebView2Async(
            Microsoft.UI.Xaml.Controls.WebView2 webView,
            string? initialQuery = null,
            bool followSystemTheme = true,
            ElementTheme fixedTheme = ElementTheme.Default)
        {
            if (webView == null) return;
            await webView.EnsureCoreWebView2Async();
            var core = webView.CoreWebView2;

            Func<bool> isDarkNow = () =>
                followSystemTheme ? (webView.ActualTheme == ElementTheme.Dark)
                                  : (fixedTheme == ElementTheme.Dark);

            core.NavigationStarting += (s, e) =>
            {
                try
                {
                    var uri = e.Uri ?? "";
                    if (uri.Contains("bing.com/dict", StringComparison.OrdinalIgnoreCase) &&
                        uri.Contains("q=welcome", StringComparison.OrdinalIgnoreCase))
                    {
                        e.Cancel = true;
                        var target = !string.IsNullOrWhiteSpace(initialQuery)
                            ? $"https://cn.bing.com/dict/search?q={Uri.EscapeDataString(initialQuery)}"
                            : "https://cn.bing.com/dict/";
                        core.Navigate(target);
                        return;
                    }

                    var defaultUA = core.Settings.UserAgent;
                    if (!defaultUA.Contains("TranslatorApp"))
                        core.Settings.UserAgent = defaultUA + " TranslatorApp/1.0";

                    core.Profile.PreferredColorScheme = isDarkNow()
                        ? CoreWebView2PreferredColorScheme.Dark
                        : CoreWebView2PreferredColorScheme.Light;
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
                        core.Profile.PreferredColorScheme = dark
                            ? CoreWebView2PreferredColorScheme.Dark
                            : CoreWebView2PreferredColorScheme.Light;

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
                    var writer = new DataWriter(ras) {UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8, ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian };
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
                    var writer = new DataWriter(ras)
                    {
                        UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8,
                        ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian
                    }; writer.WriteBytes(Encoding.UTF8.GetBytes(cssText));
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
                    var writer = new DataWriter(ras) {UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8, ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian };
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
                    var writer = new DataWriter(ras)
                    {
                        UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8,
                        ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian
                    }; writer.WriteBytes(Encoding.UTF8.GetBytes(cssText));
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

            if (!string.IsNullOrEmpty(initialQuery))
                core.Navigate($"https://cn.bing.com/dict/search?q={Uri.EscapeDataString(initialQuery)}");
        }

        // ---------- 标题栏拖拽区：官方推荐的最简实时写法 ----------
        public void RegisterWindowTitleBar(UIElement dragElement, FrameworkElement? customRegion = null, FrameworkElement? accountElement = null, FrameworkElement? centerHost = null)
        {
            if (dragElement == null) throw new ArgumentNullException(nameof(dragElement));
            _dragElement = dragElement;
            _customRegion = customRegion;
            _accountElement = accountElement;
            _centerHost = centerHost;

            EnsureTitleBarSubscriptions();
            _ = UpdateLayoutAndRegisterAsync();
        }

        private void EnsureTitleBarSubscriptions()
        {
            if (_titlebarRegistered) return;

            try
            {
                _coreTitleBar = CoreApplication.GetCurrentView().TitleBar;
                _coreTitleBar.ExtendViewIntoTitleBar = true; // 扩展到应用标题栏区域

                _coreTitleBar.LayoutMetricsChanged += (s, e) => _ = UpdateLayoutAndRegisterAsync(); // 系统按钮区域变化
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[App] CoreTitleBar subscribe failed: " + ex);
            }

            try
            {
                var di = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
                di.DpiChanged += (s, e) => _ = UpdateLayoutAndRegisterAsync(); // DPI 改变
            }
            catch { }

            try
            {
                Window.Current.CoreWindow.SizeChanged += (s, e) => _ = UpdateLayoutAndRegisterAsync(); // 窗口大小变化
            }
            catch { }

            _titlebarRegistered = true;
        }

        private void UpdateInsetsAndScale()
        {
            try
            {
                if (_coreTitleBar == null) _coreTitleBar = CoreApplication.GetCurrentView().TitleBar;
                var di = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
                _rawPixelsPerViewPixel = di.RawPixelsPerViewPixel;

                var rawR = _coreTitleBar?.SystemOverlayRightInset ?? 0;
                var rawL = _coreTitleBar?.SystemOverlayLeftInset ?? 0;

                // 转为逻辑像素
                if (_rawPixelsPerViewPixel > 0)
                {
                    _rightInset = rawR / _rawPixelsPerViewPixel;
                    _leftInset = rawL / _rawPixelsPerViewPixel;
                }
                else
                {
                    _rightInset = rawR;
                    _leftInset = rawL;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[App UpdateInsetsAndScale] " + ex);
                _rightInset = 0;
                _leftInset = 0;
            }
        }

        private async Task UpdateLayoutAndRegisterAsync()
        {
            try
            {
                if (_dragElement == null) return;

                UpdateInsetsAndScale();
                await Task.Yield();

                double leftExclude = _leftInset;
                double rightExclude = _rightInset;

                // 把右侧账号区域作为避让（不拉伸，也不覆盖系统按钮区）
                try
                {
                    if (_accountElement != null && _accountElement.ActualWidth > 0)
                        rightExclude += _accountElement.ActualWidth + _accountElement.Margin.Left + _accountElement.Margin.Right;
                }
                catch { }

                double totalWidth = Window.Current.Bounds.Width;
                const double MinDragWidth = 48.0;
                // 保护拖拽区最小横向长度
                if (totalWidth - (leftExclude + rightExclude) < MinDragWidth)
                    rightExclude = Math.Max(0, totalWidth - MinDragWidth - leftExclude);

                // 设置拖拽区横向 Margin（官方推荐要点：实时根据 inset 更新）
                if (_dragElement is FrameworkElement fe)
                {
                    fe.Margin = new Windows.UI.Xaml.Thickness(leftExclude, 0, rightExclude, 0);
                    fe.HorizontalAlignment = HorizontalAlignment.Stretch;
                    fe.VerticalAlignment = VerticalAlignment.Top;
                }

                // 中间区域只居中，不拉伸
                if (_centerHost != null)
                    _centerHost.HorizontalAlignment = HorizontalAlignment.Center;

                // 统一注册为标题栏命中区域（在变化时重复调用是允许的）
                Window.Current.SetTitleBar(_dragElement);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[App UpdateLayoutAndRegisterAsync] " + ex);
            }
        }

        public void RefreshTitleBarNow() => _ = UpdateLayoutAndRegisterAsync();

        // ---------- 统一主题 + Backdrop ----------
        private static bool IsSystemInDarkMode()
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

            RefreshTitleBarNow();
        }
    }
}
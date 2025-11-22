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

        private UIElement? _dragElement;
        private FrameworkElement? _customRegion;
        private FrameworkElement? _accountElement;
        private FrameworkElement? _centerHost;
        private CoreApplicationViewTitleBar? _coreTitleBar;
        private bool _titlebarRegistered = false;
        private double _leftInset = 0;
        private double _rightInset = 0;
        private double _rawPixelsPerViewPixel = 1.0;

        public App()
        {
            InitializeComponent();
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        }

        public async Task SignOutAsync()
        {
            try
            {
                // 删除本地缓存文件
                if (File.Exists(CacheFilePath))
                    File.Delete(CacheFilePath);

                // 清空 GraphClient
                GraphClient = null;

                Debug.WriteLine("[App] 已退出 Microsoft 账号");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[App] 退出流程异常: " + ex);
            }
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
                // 应用注册信息
                var clientId = "63de19bb-351b-40e8-8a37-cb657eb6e685";
                var redirectUri = "myapp://auth";
                var scopes = "User.Read Files.ReadWrite.AppFolder";

                // 构造授权请求 URL
                var authorizeUrl =
                    $"https://login.microsoftonline.com/common/oauth2/v2.0/authorize" +
                    $"?client_id={clientId}" +
                    $"&response_type=code" +
                    $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                    $"&response_mode=query" +
                    $"&scope={Uri.EscapeDataString(scopes)}";

                // 调用系统账户选择器 (WebAuthenticationBroker)
                var result = await WebAuthenticationBroker.AuthenticateAsync(
                    WebAuthenticationOptions.None,
                    new Uri(authorizeUrl),
                    new Uri(redirectUri));

                if (result.ResponseStatus == WebAuthenticationStatus.Success)
                {
                    // 从返回的 URI 中解析授权码
                    var responseUri = new Uri(result.ResponseData);
                    var queryParams = System.Web.HttpUtility.ParseQueryString(responseUri.Query);
                    var code = queryParams["code"];

                    if (!string.IsNullOrEmpty(code))
                    {
                        // 用授权码换取 Access Token
                        using (var http = new HttpClient())
                        {
                            var tokenResponse = await http.PostAsync(
                                "https://login.microsoftonline.com/common/oauth2/v2.0/token",
                                new FormUrlEncodedContent(new Dictionary<string, string>
                                {
                            { "client_id", clientId },
                            { "scope", scopes },
                            { "code", code },
                            { "redirect_uri", redirectUri },
                            { "grant_type", "authorization_code" }
                                }));

                            var json = await tokenResponse.Content.ReadAsStringAsync();

                            // 使用 System.Text.Json 解析
                            using var doc = JsonDocument.Parse(json);
                            string accessToken = doc.RootElement.GetProperty("access_token").GetString();

                            // 用 Access Token 初始化 GraphClient
                            var tokenProvider = new BaseBearerTokenAuthenticationProvider(
                                new DelegateTokenProvider((_, __) => Task.FromResult(accessToken)));
                            GraphClient = new GraphServiceClient(tokenProvider);

                            await FetchAndUpdateAccountUiAsync();
                        }
                    }
                }
                else if (result.ResponseStatus == WebAuthenticationStatus.ErrorHttp)
                {
                    Debug.WriteLine("[App] 登录失败，HTTP 错误: " + result.ResponseErrorDetail);
                }
                else
                {
                    Debug.WriteLine("[App] 登录取消或失败: " + result.ResponseStatus);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[App] 登录流程异常: " + ex);
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
                        RefreshTitleBarNow();
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
                // 首选启动尺寸（可选）——DIP 单位
                try
                {
                    // 首选最小尺寸（DIP 单位）
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
                // 激活窗口（在设置首选最小尺寸之后）
                Window.Current.Activate();

                // 延迟再次应用，以覆盖可能的系统延迟或主题刷新问题
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
                _coreTitleBar.ExtendViewIntoTitleBar = true;
                _coreTitleBar.LayoutMetricsChanged += (s, e) => _ = UpdateLayoutAndRegisterAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[App] CoreTitleBar subscribe failed: " + ex);
            }
            try
            {
                var di = Windows.Graphics.Display.DisplayInformation.GetForCurrentView();
                di.DpiChanged += (s, e) => _ = UpdateLayoutAndRegisterAsync();
            }
            catch { }
            try
            {
                Window.Current.CoreWindow.SizeChanged += (s, e) => _ = UpdateLayoutAndRegisterAsync();
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

                // 不再把账号宽度叠加到 rightExclude（这样会缩小拖拽区）
                // 但我们需要把账号元素本身往左偏移，保证它不会落在系统按钮下
                // 计算最小拖拽区保护
                double totalWidth = Window.Current.Bounds.Width;
                const double MinDragWidth = 48.0;
                if (totalWidth - (leftExclude + rightExclude) < MinDragWidth)
                    rightExclude = Math.Max(0, totalWidth - MinDragWidth - leftExclude);

                // ---- 核心修复：让 DragRegion 从 leftExclude 开始，宽度延伸到窗口最右边 ----
                if (_dragElement is FrameworkElement feDrag)
                {
                    // 左侧留出系统 inset，右侧通过设置宽度延伸到窗口最右
                    feDrag.Margin = new Windows.UI.Xaml.Thickness(leftExclude, 0, 0, 0);
                    feDrag.HorizontalAlignment = HorizontalAlignment.Left;
                    feDrag.VerticalAlignment = VerticalAlignment.Top;

                    // 宽度 = 窗口宽 - leftExclude（视觉上覆盖到窗口最右边）
                    feDrag.Width = Math.Max(0, totalWidth - leftExclude);

                    // 保持高度为系统标题栏高度或至少 48
                    double titleHeight = Math.Max(48.0, _coreTitleBar?.Height ?? 48.0);
                    feDrag.Height = titleHeight;
                    feDrag.MinHeight = titleHeight;
                }

                // 将 customRegion（整段标题栏容器）padding 调整为系统 inset（视觉内缩，避免内容被系统按钮压住）
                if (_customRegion is Control ctrl)
                {
                    ctrl.Padding = new Windows.UI.Xaml.Thickness(_leftInset, 0, _rightInset, 0);
                }

                // 关键：把账号元素右侧 margin 设置为 _rightInset + 用户留白（例如 12）
                try
                {
                    if (_accountElement != null)
                    {
                        var m = _accountElement.Margin;
                        double desiredRightGap = 12.0; // 你希望头像离系统按钮留出的间距
                        _accountElement.HorizontalAlignment = HorizontalAlignment.Right;
                        _accountElement.Margin = new Windows.UI.Xaml.Thickness(m.Left, m.Top, _rightInset + desiredRightGap, m.Bottom);

                        // 确保头像在视觉上高于拖拽区
                        Canvas.SetZIndex(_accountElement, 2);

                        // 让头像垂直对齐并与标题高度协调
                        if (_accountElement is FrameworkElement fae)
                        {
                            double titleHeight = Math.Max(48.0, _coreTitleBar?.Height ?? 48.0);
                            fae.Height = Math.Max(28.0, titleHeight - 12.0);
                            fae.VerticalAlignment = VerticalAlignment.Center;
                        }
                    }
                }
                catch { }

                if (_centerHost != null)
                    _centerHost.HorizontalAlignment = HorizontalAlignment.Center;

                Window.Current.SetTitleBar(_dragElement);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[App UpdateLayoutAndRegisterAsync] " + ex);
            }
        }
        public void RefreshTitleBarNow() => _ = UpdateLayoutAndRegisterAsync();

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
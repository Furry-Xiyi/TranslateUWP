using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace TranslatorApp.Pages
{
    public sealed partial class WordLookupPage : Page
    {
        private string _currentQuery = string.Empty;
        private readonly List<string> _history = new();

        private bool _bingDictReady = false;
        private string? _pendingBingQuery;

        private static readonly HttpClient _http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TranslatorApp/1.0 (+https://github.com/Furry-Xiyi/TranslatorApp)");
            return client;
        }

        public WordLookupPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Required;
            InitWebViewAsync();
            this.Loaded += (_, __) => TryBindTitleBarControls();
        }

        private async void InitWebViewAsync()
        {
            try
            {
                // UWP WebView 不需要 EnsureCoreWebView2Async；使用内置 WebView 控件
            }
            catch { }
        }

        private void LoadHistory() => _history.Clear();
        private void SaveHistory() => Services.SettingsService.LookupHistory = new List<string>(_history);

        public void AddToHistory(string term)
        {
            if (string.IsNullOrWhiteSpace(term)) return;
            if (!_history.Contains(term))
            {
                _history.Insert(0, term);
                if (_history.Count > 50) _history.RemoveAt(_history.Count - 1);
                SaveHistory();
            }
        }

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var text = sender.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text)) { sender.ItemsSource = null; return; }
            var suggestions = new List<string>();
            suggestions.AddRange(_history.FindAll(h => h.StartsWith(text, StringComparison.OrdinalIgnoreCase)));
            suggestions.AddRange(new[] { text, $"{text} meaning", $"{text} 翻译", $"{text} 用法" });
            sender.ItemsSource = suggestions;
        }

        private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            var q = args.QueryText?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(q)) return;
            _currentQuery = q;
            AddToHistory(q);
            NavigateToSite(q);
        }

        public void NavigateToSite(string query)
        {
            ToggleDailySkeleton(false);
            DailySentenceCard.Visibility = Visibility.Collapsed;

            Web.Visibility = Visibility.Visible;
            WebMask.Visibility = Visibility.Visible;
            FabFavorite.Visibility = Visibility.Visible;

            if (MainPage.Current != null)
            {
                MainPage.Current.IsInLookupMode = true;
                MainPage.Current.ShowCloseButton();
            }

            var site = (MainPage.Current?.LookupSiteComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                       ?? Services.SettingsService.LastLookupSite
                       ?? "Youdao";

            Services.SettingsService.LastLookupSite = site;

            if (site == "Bing")
            {
                if (!_bingDictReady)
                {
                    _pendingBingQuery = query;
                    Web.Opacity = 0;
                    Web.NavigationCompleted += Web_NavigationCompleted;
                    SafeSetWebSource("https://cn.bing.com/dict/");
                    return;
                }
            }

            string url = site switch
            {
                "Google" => $"https://www.google.com/search?q=define%3A{Uri.EscapeDataString(query)}",
                "Youdao" => $"https://dict.youdao.com/result?word={Uri.EscapeDataString(query)}&lang=en",
                _ => $"https://dict.youdao.com/result?word={Uri.EscapeDataString(query)}&lang=en"
            };
            SafeSetWebSource(url);
        }

        private void Web_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            WebMask.Visibility = Visibility.Collapsed;

            try
            {
                var src = sender.Source?.ToString() ?? sender.CoreWebView2?.Source;
                Uri? uri = null;
                if (!string.IsNullOrEmpty(src))
                {
                    uri = new Uri(src);
                }
                else
                {
                    uri = Web.Source;
                }

                if (uri != null)
                {
                    var qs = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    var newTerm = qs["q"] ?? qs["word"] ?? qs["wd"];

                    if (string.IsNullOrWhiteSpace(newTerm))
                    {
                        var segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                        var idx = Array.IndexOf(segments, "result");
                        if (idx >= 0 && idx < segments.Length - 1)
                            newTerm = segments[idx + 1];
                    }

                    if (!string.IsNullOrWhiteSpace(newTerm))
                        _currentQuery = Uri.UnescapeDataString(newTerm);
                }
            }
            catch
            {
                // 忽略解析错误
            }

            if (!string.IsNullOrWhiteSpace(_currentQuery))
                FabFavorite.Visibility = Visibility.Visible;

            MainPage.Current?.ShowCloseButton();

            // 如果这是热身订阅（_pendingBingQuery 场景），退订以避免重复触发
            try
            {
                sender.NavigationCompleted -= Web_NavigationCompleted;
            }
            catch { }
        }

        private void FabFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_currentQuery))
            {
                Services.FavoritesService.Add(_currentQuery);
                Services.FavoritesService.Save();
                MainPage.Current?.ShowInfo("已收藏");
            }
        }

        public void ReturnToDailySentence()
        {
            TearDownWebView();
            _currentQuery = string.Empty;
            DailySentenceCard.Visibility = Visibility.Visible;
            FabFavorite.Visibility = Visibility.Collapsed;
            if (MainPage.Current != null)
            {
                MainPage.Current.IsInLookupMode = false;
                if (MainPage.Current.LookupCloseButton != null) MainPage.Current.LookupCloseButton.Visibility = Visibility.Collapsed;
            }
        }

        private void TryBindTitleBarControls()
        {
            if (MainPage.Current == null) return;
            try
            {
                MainPage.Current.EnsureTitleBarControls();
                var cbSite = MainPage.Current.LookupSiteComboBox;
                var searchBox = MainPage.Current.LookupSearchBox;
                if (cbSite != null && searchBox != null)
                {
                    cbSite.SelectionChanged -= LookupSite_SelectionChanged;
                    cbSite.SelectionChanged += LookupSite_SelectionChanged;
                    searchBox.TextChanged -= SearchBox_TextChanged;
                    searchBox.TextChanged += SearchBox_TextChanged;
                    searchBox.QuerySubmitted -= SearchBox_QuerySubmitted;
                    searchBox.QuerySubmitted += SearchBox_QuerySubmitted;
                }
            }
            catch { }
        }

        private void LookupSite_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var site = (MainPage.Current?.LookupSiteComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (!string.IsNullOrEmpty(site)) Services.SettingsService.LastLookupSite = site;
            var text = MainPage.Current?.LookupSearchBox?.Text;
            if (!string.IsNullOrWhiteSpace(text)) NavigateToSite(text);
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is string term && !string.IsNullOrWhiteSpace(term))
            {
                _currentQuery = term; AddToHistory(term);
                DailySentenceCard.Visibility = Visibility.Collapsed; FabFavorite.Visibility = Visibility.Visible;
                NavigateToSite(term); return;
            }
            _ = InitializeStartupNavigation(e.Parameter);
        }

        private async Task InitializeStartupNavigation(object? navParam)
        {
            if (navParam is string term && !string.IsNullOrWhiteSpace(term))
            {
                ToggleDailySkeleton(false); DailySentenceCard.Visibility = Visibility.Collapsed; _currentQuery = term; AddToHistory(term); NavigateToSite(term); await Task.Delay(50); Web.Opacity = 1; return;
            }

            var app = (App)Application.Current;
            var cached = app.CachedDailySentence;

            bool cacheUsable = cached != null && !string.IsNullOrWhiteSpace(cached.Caption) && !string.IsNullOrWhiteSpace(cached.En);
            bool hasImageInView = ImgPic.Source != null;
            bool hasPicUrl = !string.IsNullOrEmpty(cached?.PicUrl);

            if (cacheUsable)
            {
                if (hasImageInView)
                {
                    TxtCaption.Text = cached!.Caption; TxtCaption.Visibility = Visibility.Visible;
                    TxtDate.Text = cached.Date; TxtDate.Visibility = Visibility.Visible;
                    TxtEn.Text = cached.En; ControlPanel.Visibility = Visibility.Visible;
                    TxtZh.Text = cached.Zh; TxtZh.Visibility = Visibility.Visible;

                    CaptionShimmer.Visibility = Visibility.Collapsed; DateShimmer.Visibility = Visibility.Collapsed;
                    EnShimmer.Visibility = Visibility.Collapsed; ZhShimmer.Visibility = Visibility.Collapsed;
                    ImageShimmer.Visibility = Visibility.Collapsed;
                    Web.Opacity = 1; return;
                }

                bool needImageSkeleton = hasPicUrl && !hasImageInView;
                ToggleDailySkeleton(true, includeImage: needImageSkeleton);
                await Task.Delay(220); await ApplyDailySentenceDataAsync(cached!);
            }
            else
            {
                ToggleDailySkeleton(true, includeImage: true);
                DailySentenceData? daily = null;
                try { daily = await GetDailySentenceData(); } catch { }
                if (daily != null) { app.CachedDailySentence = daily; await Task.Delay(800); await ApplyDailySentenceDataAsync(daily); }
                else { ToggleDailySkeleton(false); DailySentenceCard.Visibility = Visibility.Collapsed; }
            }

            Web.Opacity = 1;
        }

        private async Task<DailySentenceData?> GetDailySentenceData()
        {
            try
            {
                using var client = new HttpClient();
                var json = await client.GetStringAsync("https://open.iciba.com/dsapi/");
                using var doc = JsonDocument.Parse(json);
                var caption = doc.RootElement.TryGetProperty("caption", out var cap) ? cap.GetString() ?? "" : "";
                var date = doc.RootElement.TryGetProperty("dateline", out var dl) ? dl.GetString() ?? "" : "";
                var en = doc.RootElement.GetProperty("content").GetString() ?? "";
                var zh = doc.RootElement.GetProperty("note").GetString() ?? "";
                var tts = doc.RootElement.TryGetProperty("tts", out var t) ? t.GetString() ?? "" : "";
                var pic = (doc.RootElement.TryGetProperty("picture2", out var p2) ? p2.GetString() : null) ?? (doc.RootElement.TryGetProperty("picture", out var p1) ? p1.GetString() : null) ?? "";
                return new DailySentenceData { Caption = caption, Date = date, En = en, Zh = zh, PicUrl = pic, TtsUrl = tts };
            }
            catch { return null; }
        }

        private async Task ApplyDailySentenceDataAsync(DailySentenceData data)
        {
            DailySentenceCard.Visibility = Visibility.Visible;
            TxtCaption.Text = data.Caption; TxtCaption.Visibility = Visibility.Visible; CaptionShimmer.Visibility = Visibility.Collapsed;
            TxtDate.Text = data.Date; TxtDate.Visibility = Visibility.Visible; DateShimmer.Visibility = Visibility.Collapsed;
            TxtEn.Text = data.En; ControlPanel.Visibility = Visibility.Visible; EnShimmer.Visibility = Visibility.Collapsed;
            TxtZh.Text = data.Zh; TxtZh.Visibility = Visibility.Visible; ZhShimmer.Visibility = Visibility.Collapsed;

            BtnPlayTts.Click -= BtnPlayTts_Click;
            if (!string.IsNullOrEmpty(data.TtsUrl)) BtnPlayTts.Click += BtnPlayTts_Click;

            if (!string.IsNullOrWhiteSpace(data.PicUrl)) { if (ImgPic.Source != null) { ImgPic.Visibility = Visibility.Visible; ImageShimmer.Visibility = Visibility.Collapsed; } else await SetImageAsync(data.PicUrl); }
            else { ImageShimmer.Visibility = Visibility.Collapsed; ImgPic.Visibility = Visibility.Collapsed; }

            try
            {
                DailySentenceCard.Opacity = 0;
                DailySentenceCard.RenderTransform = new Windows.UI.Xaml.Media.TranslateTransform { Y = 20 };

                var sb = new Windows.UI.Xaml.Media.Animation.Storyboard();
                var fadeAnim = new Windows.UI.Xaml.Media.Animation.DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(300)), EasingFunction = new Windows.UI.Xaml.Media.Animation.CubicEase { EasingMode = Windows.UI.Xaml.Media.Animation.EasingMode.EaseOut } };
                Windows.UI.Xaml.Media.Animation.Storyboard.SetTarget(fadeAnim, DailySentenceCard);
                Windows.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fadeAnim, "Opacity");
                sb.Children.Add(fadeAnim);

                var translateAnim = new Windows.UI.Xaml.Media.Animation.DoubleAnimation { From = 20, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(300)), EasingFunction = new Windows.UI.Xaml.Media.Animation.CubicEase { EasingMode = Windows.UI.Xaml.Media.Animation.EasingMode.EaseOut } };
                Windows.UI.Xaml.Media.Animation.Storyboard.SetTarget(translateAnim, DailySentenceCard);
                Windows.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(translateAnim, "(UIElement.RenderTransform).(TranslateTransform.Y)");
                sb.Children.Add(translateAnim);

                sb.Begin();
            }
            catch { }
        }

        private void BtnPlayTts_Click(object sender, RoutedEventArgs e)
        {
            var app = (App)Application.Current;
            var data = app.CachedDailySentence;
            if (string.IsNullOrEmpty(_currentQuery) && data != null && !string.IsNullOrEmpty(data.TtsUrl))
            {
                var player = new MediaPlayer { Source = MediaSource.CreateFromUri(new Uri(data.TtsUrl)) };
                player.Play();
            }
        }

        private void ToggleDailySkeleton(bool isLoading, bool includeImage = true)
        {
            if (isLoading)
            {
                DailySentenceCard.Visibility = Visibility.Visible;
                CaptionShimmer.Visibility = Visibility.Visible; DateShimmer.Visibility = Visibility.Visible; EnShimmer.Visibility = Visibility.Visible; ZhShimmer.Visibility = Visibility.Visible;
                if (includeImage) { ImageShimmer.Visibility = Visibility.Visible; ImgPic.Visibility = Visibility.Collapsed; }
                TxtCaption.Visibility = Visibility.Collapsed; TxtDate.Visibility = Visibility.Collapsed; ControlPanel.Visibility = Visibility.Collapsed; TxtZh.Visibility = Visibility.Collapsed;
            }
            else
            {
                CaptionShimmer.Visibility = Visibility.Collapsed; DateShimmer.Visibility = Visibility.Collapsed; EnShimmer.Visibility = Visibility.Collapsed; ZhShimmer.Visibility = Visibility.Collapsed;
                if (includeImage) ImageShimmer.Visibility = Visibility.Collapsed;
            }
        }

        private async Task SetImageAsync(string picUrl)
        {
            ImageShimmer.Visibility = Visibility.Visible; ImgPic.Visibility = Visibility.Collapsed;
            if (string.IsNullOrWhiteSpace(picUrl)) { ImageShimmer.Visibility = Visibility.Collapsed; ImgPic.Visibility = Visibility.Collapsed; return; }

            var bmp = new BitmapImage();
            bmp.ImageOpened += (s, e) => { var _ = this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => { ImgPic.Source = bmp; ImgPic.Visibility = Visibility.Visible; ImageShimmer.Visibility = Visibility.Collapsed; }); };
            bmp.ImageFailed += (s, e) => { var _ = this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => { ImgPic.Visibility = Visibility.Collapsed; ImageShimmer.Visibility = Visibility.Collapsed; }); };

            try
            {
                if (picUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var comma = picUrl.IndexOf(',');
                    if (comma > 0 && comma < picUrl.Length - 1)
                    {
                        var base64 = picUrl[(comma + 1)..];
                        var bytes = Convert.FromBase64String(base64);
                        using var stream = new InMemoryRandomAccessStream();
                        using (var writer = new DataWriter(stream)) { writer.WriteBytes(bytes); await writer.StoreAsync(); }
                        stream.Seek(0);
                        await bmp.SetSourceAsync(stream);
                    }
                    else { ImageShimmer.Visibility = Visibility.Collapsed; ImgPic.Visibility = Visibility.Collapsed; }
                }
                else
                {
                    using var resp = await _http.GetAsync(picUrl, HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    await using var netStream = await resp.Content.ReadAsStreamAsync();
                    using var mem = new InMemoryRandomAccessStream();
                    await netStream.CopyToAsync(mem.AsStreamForWrite());
                    mem.Seek(0);
                    await bmp.SetSourceAsync(mem);
                }
            }
            catch { ImgPic.Visibility = Visibility.Collapsed; ImageShimmer.Visibility = Visibility.Collapsed; }
        }

        private void TearDownWebView()
        {
            try { Web.NavigateToString("<html></html>"); } catch { }
            Web.Source = null;
            WebMask.Visibility = Visibility.Collapsed;
            FabFavorite.Visibility = Visibility.Collapsed;
            Web.Visibility = Visibility.Collapsed;
        }

        private void SafeSetWebSource(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                try { Web.Source = uri; }
                catch (ArgumentException) { }
            }
        }

        public class DailySentenceData { public string Caption { get; set; } = ""; public string Date { get; set; } = ""; public string En { get; set; } = ""; public string Zh { get; set; } = ""; public string PicUrl { get; set; } = ""; public string TtsUrl { get; set; } = ""; }
    }
}
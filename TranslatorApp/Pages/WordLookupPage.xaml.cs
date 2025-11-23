using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace TranslatorApp.Pages
{
    public sealed partial class WordLookupPage : Page
    {
        private string _currentQuery = string.Empty;
        private readonly List<string> _history = new();
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private string _currentImageUrl = "";
        private DailySentenceData _currentData;

        public WordLookupPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Required;
            this.Loaded += WordLookupPage_Loaded;
        }

        private void WordLookupPage_Loaded(object sender, RoutedEventArgs e)
        {
            // 启动骨架屏闪光动画
            ShimmerStoryboard.Begin();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // 首次加载每日一句
            await InitializeDailySentenceAsync();

            // 如果有查词参数，则导航到查词
            if (e.Parameter is string term && !string.IsNullOrWhiteSpace(term))
            {
                _currentQuery = term;
                AddToHistory(term);
                NavigateToSite(term);
            }
        }

        private async Task InitializeDailySentenceAsync()
        {
            SkeletonLayer.Visibility = Visibility.Visible;
            ContentLayer.Visibility = Visibility.Collapsed;

            var app = (App)Application.Current;
            var data = app.CachedDailySentence ?? await GetDailySentenceData();

            if (data != null)
            {
                app.CachedDailySentence = data;
                _currentData = data;
                await ApplyDailySentenceDataAsync(data);
            }
            else
            {
                SkeletonLayer.Visibility = Visibility.Collapsed;
                MainPage.Current?.ShowError("加载每日一句失败");
            }
        }

        private async Task ApplyDailySentenceDataAsync(DailySentenceData data)
        {
            // 更新文本内容
            TxtCaption.Text = data.Caption ?? "Daily Sentence";
            TxtDate.Text = data.Date ?? DateTime.Now.ToString("yyyy-MM-dd");
            TxtEn.Text = data.En ?? "No content available";
            TxtZh.Text = data.Zh ?? "暂无翻译";
            _currentImageUrl = data.PicUrl;

            // 加载背景图和配图
            if (!string.IsNullOrEmpty(data.PicUrl))
            {
                try
                {
                    var bitmap = new BitmapImage(new Uri(data.PicUrl));
                    BackgroundImage.Source = bitmap;
                }
                catch { }
            }

            // 停止骨架屏动画
            ShimmerStoryboard.Stop();

            // 切换显示并淡入
            SkeletonLayer.Visibility = Visibility.Collapsed;
            ContentLayer.Visibility = Visibility.Visible;

            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(600),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fadeIn, ContentLayer);
            Storyboard.SetTargetProperty(fadeIn, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(fadeIn);
            sb.Begin();
        }

        // --- 查词：切换到 WebView 全屏 ---
        public void NavigateToSite(string query)
        {
            FabFavorite.Visibility = Visibility.Collapsed;
            if (string.IsNullOrWhiteSpace(query)) return;
            _currentQuery = query;

            // 隐藏每日一句，显示 WebView 容器
            DailySentenceContainer.Visibility = Visibility.Collapsed;

            WebViewContainer.Visibility = Visibility.Visible;

            // 通知主页显示关闭按钮
            MainPage.Current?.ShowCloseButton();

            // 查词时：立即隐藏保存图片按钮
            SaveImageButton.Visibility = Visibility.Collapsed;

            var site = Services.SettingsService.LastLookupSite ?? "Youdao";
            if (site == "Local")
            {
                ShowLocalDictionary(query);
                // 本地词典查词完成后立即显示收藏按钮
                FabFavorite.Visibility = Visibility.Visible;
                return;
            }

            Web.Visibility = Visibility.Visible;
            WebMask.Visibility = Visibility.Visible;
            LocalDictionaryHost.Visibility = Visibility.Collapsed;

            string url = site switch
            {
                "Google" => $"https://www.google.com/search?q=define%3A{Uri.EscapeDataString(query)}",
                "Bing" => $"https://cn.bing.com/dict/search?q={Uri.EscapeDataString(query)}",
                _ => $"https://dict.youdao.com/result?word={Uri.EscapeDataString(query)}&lang=en"
            };

            try
            {
                if (Web.Source?.ToString() != url)
                    Web.Source = new Uri(url);
            }
            catch { }
        }

        // --- 返回每日一句 ---
        public void ReturnToDailySentence()
        {
            // 隐藏 WebView，显示每日一句
            WebViewContainer.Visibility = Visibility.Collapsed;
            DailySentenceContainer.Visibility = Visibility.Visible;

            // 重置状态
            Web.Visibility = Visibility.Collapsed;
            WebMask.Visibility = Visibility.Collapsed;
            LocalDictionaryHost.Visibility = Visibility.Collapsed;

            // 收藏按钮隐藏
            FabFavorite.Visibility = Visibility.Collapsed;
            // 保存按钮重新显示
            SaveImageButton.Visibility = Visibility.Visible;

            _currentQuery = string.Empty;
            if (MainPage.Current?.LookupSearchBoxRef != null)
                MainPage.Current.LookupSearchBoxRef.Text = "";
        }

        // --- 保存图片 ---
        private async void SaveImage_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentImageUrl)) return;

            try
            {
                var bytes = await _http.GetByteArrayAsync(_currentImageUrl);
                var picker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                    SuggestedFileName = $"DailySentence_{DateTime.Now:yyyyMMdd}"
                };
                picker.FileTypeChoices.Add("Image", new List<string> { ".jpg", ".png" });

                var file = await picker.PickSaveFileAsync();
                if (file != null)
                {
                    await Windows.Storage.FileIO.WriteBytesAsync(file, bytes);
                    MainPage.Current?.ShowSuccess("图片已保存");
                }
            }
            catch
            {
                MainPage.Current?.ShowError("保存失败");
            }
        }

        // --- 刷新每日一句 ---
        private async void RefreshDailySentence_Click(object sender, RoutedEventArgs e)
        {
            var app = (App)Application.Current;
            app.CachedDailySentence = null; // 清除缓存
            await InitializeDailySentenceAsync();
            MainPage.Current?.ShowSuccess("已刷新");
        }

        // --- 分享句子 ---
        private void ShareSentence_Click(object sender, RoutedEventArgs e)
        {
            if (_currentData == null) return;

            var dataTransferManager = DataTransferManager.GetForCurrentView();
            dataTransferManager.DataRequested += (s, args) =>
            {
                var request = args.Request;
                request.Data.Properties.Title = "每日一句";
                request.Data.SetText($"{_currentData.En}\n\n{_currentData.Zh}\n\n—— {_currentData.Date}");
            };
            DataTransferManager.ShowShareUI();
        }

        // --- 播放发音 ---
        private void BtnPlayTts_Click(object sender, RoutedEventArgs e)
        {
            if (_currentData != null && !string.IsNullOrEmpty(_currentData.TtsUrl))
            {
                try
                {
                    var player = new MediaPlayer
                    {
                        Source = MediaSource.CreateFromUri(new Uri(_currentData.TtsUrl)),
                        AutoPlay = true
                    };
                }
                catch
                {
                    MainPage.Current?.ShowError("播放失败");
                }
            }
        }

        // --- WebView 导航完成 ---
        private void Web_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            WebMask.Visibility = Visibility.Collapsed;
            if (!string.IsNullOrEmpty(_currentQuery))
                FabFavorite.Visibility = Visibility.Visible;
        }

        // --- 收藏 ---
        private void FabFavorite_Click(object sender, RoutedEventArgs e)
        {
            Services.FavoritesService.Add(_currentQuery);
            MainPage.Current?.ShowSuccess("已收藏");
        }

        // --- 本地词典 ---
        private void ShowLocalDictionary(string q)
        {
            LocalDictionaryHost.Visibility = Visibility.Visible;
            Web.Visibility = Visibility.Collapsed;
            WebMask.Visibility = Visibility.Collapsed;

            LocalWordText.Text = q;
            LocalPronunciation.Text = $"/{q}/";
            LocalPartOfSpeech.Text = "n.";
        }

        // --- 历史记录 ---
        public void AddToHistory(string term)
        {
            if (!_history.Contains(term))
                _history.Insert(0, term);
        }

        // --- 数据模型 ---
        public class DailySentenceData
        {
            public string Caption { get; set; }
            public string Date { get; set; }
            public string En { get; set; }
            public string Zh { get; set; }
            public string PicUrl { get; set; }
            public string TtsUrl { get; set; }
        }

        private async Task<DailySentenceData> GetDailySentenceData()
        {
            try
            {
                var json = await _http.GetStringAsync("https://open.iciba.com/dsapi/");
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string val(string prop) =>
                    root.TryGetProperty(prop, out var el) ? (el.GetString() ?? "") : "";

                return new DailySentenceData
                {
                    Caption = val("caption"),
                    Date = val("dateline"),
                    En = val("content"),
                    Zh = val("note"),
                    PicUrl = val("picture2").Length > 0 ? val("picture2") : val("picture"),
                    TtsUrl = val("tts")
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
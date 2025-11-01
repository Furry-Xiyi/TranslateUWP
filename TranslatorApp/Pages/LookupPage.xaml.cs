// Pages/LookupPage.xaml.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace TranslatorApp.Pages
{
    public interface ILiveSearch
    {
        void OnSearchSuggestion(string text);
        void OnSearchSubmitted(string text, int engineIndex);
        void OnSearchCleared();
        void OnSearchEngineChanged(int engineIndex);
    }

    public sealed partial class LookupPage : Page, ILiveSearch
    {
        private MainPage _main;
        private string _currentWord;
        private bool _isFavorited;

        public LookupPage()
        {
            InitializeComponent();
            Loaded += LookupPage_Loaded;
        }

        private async void LookupPage_Loaded(object sender, RoutedEventArgs e)
        {
            var frame = Windows.UI.Xaml.Window.Current.Content as Frame;
            _main = frame?.Content as MainPage;

            // 初始加载每日一句
            await LoadDailySentenceAsync();
            UpdateFavoriteVisual(false);
        }

        private async Task LoadDailySentenceAsync()
        {
            try
            {
                using var http = new HttpClient();
                var json = await http.GetStringAsync("http://open.iciba.com/dsapi/");
                // 简单解析（可改用 JsonObject）
                // 字段示例：content (英文), note (中文), picture2 (图片URL)
                string GetValue(string key)
                {
                    var idx = json.IndexOf($"\"{key}\":");
                    if (idx < 0) return "";
                    var start = json.IndexOf('"', idx + key.Length + 3);
                    var end = json.IndexOf('"', start + 1);
                    return json.Substring(start + 1, end - start - 1);
                }
                var en = GetValue("content");
                var zh = GetValue("note");
                var img = GetValue("picture2");

                DailyEn.Text = en;
                DailyZh.Text = zh;
                DailyImage.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri(img));
            }
            catch
            {
                DailyEn.Text = "加载每日一句失败";
                DailyZh.Text = "请稍后重试";
            }
        }

        public void OnSearchSuggestion(string text)
        {
            // 简单联想：可从本地词库或历史中取匹配项（略）
        }

        public void OnSearchSubmitted(string text, int engineIndex)
        {
            _currentWord = text?.Trim();
            if (string.IsNullOrEmpty(_currentWord))
            {
                OnSearchCleared();
                return;
            }

            DailyCard.Visibility = Visibility.Collapsed;

            //右下角收藏按钮出现
            FavoriteButton.Visibility = Visibility.Visible;

            // 检查收藏状态
            _isFavorited = CheckFavorited(_currentWord);
            UpdateFavoriteVisual(_isFavorited);

            if (engineIndex == 0)
            {
                // 本地词典 XAML 展示
                ResultPivot.SelectedIndex = 0;
                LoadLocalDictionary(_currentWord);
            }
            else
            {
                // 网络词典：简单用 WebView 加载对应引擎页面
                ResultPivot.SelectedIndex = 1;
                var url = engineIndex switch
                {
                    1 => $"https://cn.bing.com/dict/search?q={Uri.EscapeDataString(_currentWord)}",
                    2 => $"https://dict.youdao.com/search?q={Uri.EscapeDataString(_currentWord)}",
                    3 => $"https://dict.baidu.com/s?wd={Uri.EscapeDataString(_currentWord)}",
                    _ => $"https://cn.bing.com/dict/search?q={Uri.EscapeDataString(_currentWord)}"
                };
                DictWebView.Navigate(new Uri(url));
            }
        }

        public void OnSearchCleared()
        {
            _currentWord = null;
            DailyCard.Visibility = Visibility.Visible;
            FavoriteButton.Visibility = Visibility.Collapsed;
            ResultPivot.SelectedIndex = 0;
            LocalWord.Text = "";
            LocalMeanings.ItemsSource = null;
        }

        public void OnSearchEngineChanged(int engineIndex)
        {
            // 切换后如果已经有词，重新提交
            if (!string.IsNullOrEmpty(_currentWord))
                OnSearchSubmitted(_currentWord, engineIndex);
        }

        private void LoadLocalDictionary(string word)
        {
            // 示例：伪造本地定义（你可以接入真词库）
            LocalWord.Text = word;
            LocalMeanings.ItemsSource = new List<string>
            {
                "n. 示例释义一",
                "v. 示例释义二",
                "adj. 示例释义三"
            };
        }

        private bool CheckFavorited(string word)
        {
            var settings = ApplicationData.Current.LocalSettings;
            var favorites = (settings.Values["favorites"] as string) ?? "";
            var set = new HashSet<string>(favorites.Split('|').Where(s => !string.IsNullOrWhiteSpace(s)));
            return set.Contains($"{word}::engine={GetEngineIndexForCurrent()}");
        }

        private int GetEngineIndexForCurrent()
        {
            var frame = Window.Current.Content as Frame;
            var mp = frame?.Content as MainPage;
            return (mp?.FindName("SearchEngineCombo") as ComboBox)?.SelectedIndex ?? 0;
        }

        private void UpdateFavoriteVisual(bool favorited)
        {
            FavoriteIcon.Glyph = favorited ? "\uE208" : "\uE208"; //这里可以切换为实心/空心的不同Glyph（示例同一）
            // 实心/空心可用两个不同字体图标替代，这里简化演示
        }

        private void FavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentWord)) return;

            var settings = ApplicationData.Current.LocalSettings;
            var favorites = (settings.Values["favorites"] as string) ?? "";
            var set = new HashSet<string>(favorites.Split('|').Where(s => !string.IsNullOrWhiteSpace(s)));

            var key = $"{_currentWord}::engine={GetEngineIndexForCurrent()}::ts={DateTimeOffset.Now.ToUnixTimeMilliseconds()}";
            var alreadyKeys = set.Where(s => s.StartsWith($"{_currentWord}::engine={GetEngineIndexForCurrent()}" )).ToList();

            if (!_isFavorited && !alreadyKeys.Any())
            {
                set.Add(key);
                settings.Values["favorites"] = string.Join("|", set);
                _isFavorited = true;
                UpdateFavoriteVisual(true);
                _main?.ShowSuccess("已收藏");
            }
            else
            {
                //取消收藏：移除所有该词当前引擎的记录
                foreach (var k in alreadyKeys) set.Remove(k);
                settings.Values["favorites"] = string.Join("|", set);
                _isFavorited = false;
                UpdateFavoriteVisual(false);
                _main?.ShowInfo("已取消收藏");
            }
        }
    }
}
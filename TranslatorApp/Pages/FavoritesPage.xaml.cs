// Pages/FavoritesPage.xaml.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace TranslatorApp.Pages
{
    public sealed partial class FavoritesPage : Page, ILiveSearch
    {
        private List<Item> _items = new();

        public FavoritesPage()
        {
            InitializeComponent();
            Loaded += FavoritesPage_Loaded;
        }

        private void FavoritesPage_Loaded(object sender, RoutedEventArgs e)
        {
            LoadFavorites();
        }

        private void LoadFavorites()
        {
            _items.Clear();
            var settings = ApplicationData.Current.LocalSettings;
            var words = (settings.Values["favorites"] as string) ?? "";
            var textFavs = (settings.Values["favorites_text"] as string) ?? "";

            foreach (var s in words.Split('|').Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                var parts = s.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries);
                var word = parts.FirstOrDefault();
                var tsPart = parts.FirstOrDefault(p => p.StartsWith("ts="));
                var ts = tsPart != null ? long.Parse(tsPart.Substring(3)) : 0;
                _items.Add(new Item { Title = word, Subtitle = $"词条 收藏于 {DateTimeOffset.FromUnixTimeMilliseconds(ts)}", IsWord = true, Raw = s });
            }
            foreach (var s in textFavs.Split('|').Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                var parts = s.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries);
                var input = parts.Length > 1 ? parts[1] : "";
                var output = parts.Length > 2 ? parts[2] : "";
                var tsPart = parts.FirstOrDefault(p => p.StartsWith("ts="));
                var ts = tsPart != null ? long.Parse(tsPart.Substring(3)) : 0;
                _items.Add(new Item { Title = output, Subtitle = $"翻译自：{input} 收藏于 {DateTimeOffset.FromUnixTimeMilliseconds(ts)}", IsWord = false, Raw = s });
            }

            ApplySort();
        }

        private void ApplySort()
        {
            var sel = SortCombo.SelectedIndex;
            IEnumerable<Item> sorted = _items;
            switch (sel)
            {
                case 0: sorted = _items.OrderBy(i => i.Timestamp); break;
                case 1: sorted = _items.OrderByDescending(i => i.Timestamp); break;
                case 2: sorted = _items.OrderBy(i => i.Title); break;
                case 3: sorted = _items.OrderByDescending(i => i.Title); break;
                default: break;
            }
            FavList.ItemsSource = sorted.ToList();
        }

        private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplySort();

        private void ViewList_Checked(object sender, RoutedEventArgs e)
        {
            // 简化：不同视图样式可通过不同 ItemTemplate/ItemsPanel 切换（此处留空）
        }
        private void ViewTiles_Checked(object sender, RoutedEventArgs e) { }
        private void ViewGrid_Checked(object sender, RoutedEventArgs e) { }

        private void FavList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = (Item)e.ClickedItem;
            if (item.IsWord)
            {
                // 跳转到查词页并使用收藏时的引擎
                var frame = Window.Current.Content as Frame;
                var root = frame?.Content as MainPage;
                root?.ShowInfo("将跳转到查词页并复用收藏时引擎");
                // 直接导航
                var navView = (root.FindName("NavView") as Microsoft.UI.Xaml.Controls.NavigationView);
                navView.SelectedItem = navView.MenuItems.OfType<Microsoft.UI.Xaml.Controls.NavigationViewItem>().First(mi => (string)mi.Tag == "Lookup");
                var lookup = (LookupPage)(root.FindName("ContentFrame") as Frame)?.Content;
                // 恢复词与引擎（简化：从Raw解析 engine=）
                var engineIdx = 0;
                var enginePart = item.Raw.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(p => p.StartsWith("engine="));
                if (enginePart != null) int.TryParse(enginePart.Substring(7), out engineIdx);
                lookup?.OnSearchSubmitted(item.Title, engineIdx);
            }
            else
            {
                // 翻译条目可以跳转到互译页并填入左右文本
                var frame = Window.Current.Content as Frame;
                var root = frame?.Content as MainPage;
                var navView = (root.FindName("NavView") as Microsoft.UI.Xaml.Controls.NavigationView);
                navView.SelectedItem = navView.MenuItems.OfType<Microsoft.UI.Xaml.Controls.NavigationViewItem>().First(mi => (string)mi.Tag == "Translate");
                var page = (TranslatePage)(root.FindName("ContentFrame") as Frame)?.Content;
                // 简化：不还原全部信息，只演示跳转
                root?.ShowInfo("已跳转到互译页");
            }
        }

        public void OnSearchSuggestion(string text)
        {
            // 标题栏搜索联动：筛选收藏
            var filtered = _items.Where(i => i.Title?.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            FavList.ItemsSource = filtered;
        }

        public void OnSearchSubmitted(string text, int engineIndex)
        {
            OnSearchSuggestion(text);
        }

        public void OnSearchCleared()
        {
            FavList.ItemsSource = _items;
        }

        public void OnSearchEngineChanged(int engineIndex) { }

        private class Item
        {
            public string Title { get; set; }
            public string Subtitle { get; set; }
            public bool IsWord { get; set; }
            public string Raw { get; set; }
            public long Timestamp
            {
                get
                {
                    var tsPart = Raw?.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(p => p.StartsWith("ts="));
                    if (tsPart == null) return 0;
                    return long.TryParse(tsPart.Substring(3), out var t) ? t : 0;
                }
            }
        }
    }
}
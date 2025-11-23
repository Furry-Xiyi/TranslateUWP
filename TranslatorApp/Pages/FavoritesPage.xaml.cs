using System;
using System.Collections.ObjectModel;
using System.Linq;
using TranslatorApp.Models;
using TranslatorApp.Services;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace TranslatorApp.Pages
{
    public sealed partial class FavoritesPage : Page
    {
        public ObservableCollection<FavoriteItem> Favorites { get; }

        public FavoritesPage()
        {
            FavoritesService.Load();
            Favorites = FavoritesService.Items ?? new ObservableCollection<FavoriteItem>();

            InitializeComponent();
            List.ItemsSource = Favorites;
            ApplySorting();

            Loaded += (_, __) => UpdateIndicator(false);
            SizeChanged += (_, __) => UpdateIndicator(false);
        }

        public enum ViewMode { List, Tile, Grid }
        public static ViewMode CurrentViewMode = ViewMode.List;

        private void ListBtn_Click(object sender, RoutedEventArgs e) => SwitchView(ViewMode.List);
        private void TileBtn_Click(object sender, RoutedEventArgs e) => SwitchView(ViewMode.Tile);
        private void GridBtn_Click(object sender, RoutedEventArgs e) => SwitchView(ViewMode.Grid);

        private void SwitchView(ViewMode mode)
        {
            CurrentViewMode = mode;
            UpdateIndicator(true);

            // 安全切换：先清空，避免 Template 和 Panel 不匹配导致的崩溃
            List.ItemsSource = null;

            switch (mode)
            {
                case ViewMode.List:
                    List.ItemsPanel = (ItemsPanelTemplate)Resources["ListItemsPanel"];
                    List.ItemTemplate = (DataTemplate)Resources["ListItemTemplate"];
                    break;
                case ViewMode.Tile:
                    List.ItemsPanel = (ItemsPanelTemplate)Resources["TileItemsPanel"];
                    List.ItemTemplate = (DataTemplate)Resources["TileItemTemplate"];
                    break;
                case ViewMode.Grid:
                    List.ItemsPanel = (ItemsPanelTemplate)Resources["GridItemsPanel"];
                    List.ItemTemplate = (DataTemplate)Resources["GridItemTemplate"];
                    break;
            }

            // 重新绑定数据
            List.ItemsSource = Favorites;
        }

        // 保持原来的指示器动画逻辑，稍作精简
        private void UpdateIndicator(bool animate)
        {
            var target = CurrentViewMode switch { ViewMode.Tile => TileBtn, ViewMode.Grid => GridBtn, _ => ListBtn };
            if (target == null || Indicator == null) return;

            var transform = target.TransformToVisual(ListBtn.Parent as UIElement);
            var x = transform.TransformPoint(new Point(0, 0)).X + target.ActualWidth / 2 - Indicator.ActualWidth / 2;

            if (animate)
            {
                var anim = new DoubleAnimation { To = x, Duration = TimeSpan.FromMilliseconds(300), EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut } };
                Storyboard.SetTarget(anim, IndicatorTranslate);
                Storyboard.SetTargetProperty(anim, "X");
                var sb = new Storyboard();
                sb.Children.Add(anim);
                sb.Begin();
            }
            else
            {
                IndicatorTranslate.X = x;
            }
        }

        private void List_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is FavoriteItem item) Frame.Navigate(typeof(WordLookupPage), item.Term);
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is FavoriteItem item)
            {
                FavoritesService.Remove(item); // 假设 Service 会同步更新 ObservableCollection
                // 如果 Service 没有自动更新 Collection，手动移除：
                if (Favorites.Contains(item)) Favorites.Remove(item);
                MainPage.Current?.ShowError("已删除");
            }
        }

        private void SortSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplySorting();

        private void ApplySorting()
        {
            if (SortSelector?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                var sorted = tag switch
                {
                    "NameDesc" => Favorites.OrderByDescending(f => f.Term).ToList(),
                    "DateNewest" => Favorites.OrderByDescending(f => f.AddedOn).ToList(),
                    "DateOldest" => Favorites.OrderBy(f => f.AddedOn).ToList(),
                    _ => Favorites.OrderBy(f => f.Term).ToList() // Default NameAsc
                };
                Favorites.Clear();
                foreach (var i in sorted) Favorites.Add(i);
            }
        }

        public void OnFavoritesSearchTextChanged(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) List.ItemsSource = Favorites;
            else List.ItemsSource = Favorites.Where(f => f.Term.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        public void OnFavoritesSearchQuerySubmitted(string? text) => OnFavoritesSearchTextChanged(text);
    }
}

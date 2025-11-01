using System;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using TranslatorApp.Models;
using TranslatorApp.Services;

namespace TranslatorApp.Pages
{
    public sealed partial class FavoritesPage : Page
    {
        public ObservableCollection<FavoriteItem> Favorites { get; }

        public FavoritesPage()
        {
            // 加载本地缓存并绑定集合
            FavoritesService.Load();
            Favorites = FavoritesService.Items ?? new ObservableCollection<FavoriteItem>();

            InitializeComponent();

            List.ItemsSource = Favorites;
            ApplySorting();

            // 初始化指示条位置
            Loaded += (_, __) =>
            {
                FrameworkElement target = CurrentViewMode switch
                {
                    ViewMode.Tile => TileBtn,
                    ViewMode.Grid => GridBtn,
                    _ => ListBtn
                };
                MoveIndicatorTo(target, animate: false);
            };

            // 窗口尺寸变化时重新定位指示条
            SizeChanged += (_, __) =>
            {
                FrameworkElement target = CurrentViewMode switch
                {
                    ViewMode.Tile => TileBtn,
                    ViewMode.Grid => GridBtn,
                    _ => ListBtn
                };
                MoveIndicatorTo(target, animate: false);
            };
        }

        public enum ViewMode { List, Tile, Grid }
        public static ViewMode CurrentViewMode = ViewMode.List;

        private void ListBtn_Click(object sender, RoutedEventArgs e)
        {
            ApplyListView();
            CurrentViewMode = ViewMode.List;
            MoveIndicatorTo(ListBtn);
        }

        private void TileBtn_Click(object sender, RoutedEventArgs e)
        {
            ApplyTileView();
            CurrentViewMode = ViewMode.Tile;
            MoveIndicatorTo(TileBtn);
        }

        private void GridBtn_Click(object sender, RoutedEventArgs e)
        {
            ApplyGridView();
            CurrentViewMode = ViewMode.Grid;
            MoveIndicatorTo(GridBtn);
        }

        private void ApplyGridView()
        {
            if (Resources.TryGetValue("GridItemsPanel", out var gridItemsPanel) && gridItemsPanel is ItemsPanelTemplate gip)
                List.ItemsPanel = gip;

            if (Resources.TryGetValue("GridItemTemplate", out var git) && git is DataTemplate dgt)
                List.ItemTemplate = dgt;

            if (Resources.TryGetValue("GridListViewItemStyle", out var gstyle) && gstyle is Style gs)
                List.ItemContainerStyle = gs;
            else
                List.ItemContainerStyle = null;

            if (List.ItemsSource == null)
                List.ItemsSource = Favorites;
        }

        private Storyboard? _indicatorStoryboard;
        private double _indicatorCurrentX = 0;

        private void MoveIndicatorTo(FrameworkElement target, bool animate = true)
        {
            if (target == null || ViewSwitcherRoot == null || Indicator == null || IndicatorTranslate == null)
                return;

            if (target.ActualWidth == 0 || ViewSwitcherRoot.ActualWidth == 0)
            {
                RoutedEventHandler handler = null;
                handler = (s, e) =>
                {
                    target.Loaded -= handler;
                    MoveIndicatorTo(target, animate);
                };
                target.Loaded += handler;
                return;
            }

            var transform = target.TransformToVisual(ViewSwitcherRoot);
            var origin = transform.TransformPoint(new Point(0, 0));
            double centerX = origin.X + target.ActualWidth / 2.0;
            double toX = Math.Max(0, centerX - Indicator.Width / 2.0);

            if (!animate)
            {
                IndicatorTranslate.X = toX;
                _indicatorCurrentX = toX;
                return;
            }

            _indicatorStoryboard?.Stop();

            var ms = 200d;
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

            var animX = new DoubleAnimation
            {
                From = _indicatorCurrentX,
                To = toX,
                Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
                EasingFunction = easing,
                EnableDependentAnimation = true
            };

            _indicatorStoryboard = new Storyboard();
            Storyboard.SetTarget(animX, IndicatorTranslate);
            Storyboard.SetTargetProperty(animX, "X");
            _indicatorStoryboard.Children.Add(animX);
            _indicatorStoryboard.Begin();

            _indicatorCurrentX = toX;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
        }

        private void List_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is FavoriteItem item)
            {
                Frame.Navigate(typeof(WordLookupPage), item.Term);
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is FavoriteItem item)
            {
                FavoritesService.Remove(item);
                MainPage.Current?.ShowError("已删除收藏");
            }
        }

        private void SortSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplySorting();
        }

        private void ApplySorting()
        {
            if (SortSelector?.SelectedItem is ComboBoxItem selected && selected.Tag is string tag)
            {
                var sorted = tag switch
                {
                    "NameAsc" => Favorites.OrderBy(f => f.Term).ToList(),
                    "NameDesc" => Favorites.OrderByDescending(f => f.Term).ToList(),
                    "DateNewest" => Favorites.OrderByDescending(f => f.AddedOn).ToList(),
                    "DateOldest" => Favorites.OrderBy(f => f.AddedOn).ToList(),
                    _ => Favorites.ToList()
                };

                Favorites.Clear();
                foreach (var item in sorted) Favorites.Add(item);
            }
        }

        private void ApplyListView()
        {
            var listPanelXaml =
                "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                "  <VirtualizingStackPanel Orientation='Vertical' />" +
                "</ItemsPanelTemplate>";
            var listItemsPanel = (ItemsPanelTemplate)XamlReader.Load(listPanelXaml);
            List.ItemsPanel = listItemsPanel;

            if (Resources.TryGetValue("ListItemTemplate", out var listItemTemplate) && listItemTemplate is DataTemplate dtemp)
                List.ItemTemplate = dtemp;

            List.ItemContainerStyle = null;
            List.HorizontalContentAlignment = HorizontalAlignment.Stretch;

            if (List.ItemsSource == null)
                List.ItemsSource = Favorites;
        }

        private void ApplyTileView()
        {
            if (Resources.TryGetValue("TileItemsPanel", out var tilePanel) && tilePanel is ItemsPanelTemplate tip)
                List.ItemsPanel = tip;

            if (Resources.TryGetValue("TileItemTemplate", out var tileTemplate) && tileTemplate is DataTemplate ttemp)
                List.ItemTemplate = ttemp;

            if (Resources.TryGetValue("TileListViewItemStyle", out var tstyle) && tstyle is Style ts)
                List.ItemContainerStyle = ts;
            else
                List.ItemContainerStyle = null;

            if (List.ItemsSource == null)
                List.ItemsSource = Favorites;
        }

        public void ApplySearchFilter(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                List.ItemsSource = Favorites;
            }
            else
            {
                var filtered = Favorites
                    .Where(f => !string.IsNullOrEmpty(f.Term) &&
                                f.Term.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                List.ItemsSource = new ObservableCollection<FavoriteItem>(filtered);
            }
        }

        public void OnFavoritesSearchTextChanged(string? text) => ApplySearchFilter(text);
        public void OnFavoritesSearchQuerySubmitted(string? text) => ApplySearchFilter(text);
    }
}
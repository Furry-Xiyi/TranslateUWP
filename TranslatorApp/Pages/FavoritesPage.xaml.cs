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
            FavoritesService.Load();
            Favorites = FavoritesService.Items ?? new ObservableCollection<FavoriteItem>();
            InitializeComponent();
            List.ItemsSource = Favorites;
            ApplySorting();
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
            try
            {
                List.ItemsSource = null;
                List.ItemsPanel = null;
                List.ItemTemplate = null;
                List.ItemContainerStyle = null;

                if (Resources["GridItemsPanel"] is ItemsPanelTemplate gip)
                    List.ItemsPanel = gip;
                if (Resources["GridItemTemplate"] is DataTemplate dgt)
                    List.ItemTemplate = dgt;
                if (Resources["GridListViewItemStyle"] is Style gs)
                    List.ItemContainerStyle = gs;
                else
                    List.ItemContainerStyle = (Style)Application.Current.Resources["DefaultListViewItemStyle"];

                List.ItemsSource = Favorites;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ApplyGridView] " + ex);
                List.ItemsSource = Favorites;
            }
        }

        private Storyboard _indicatorStoryboard;
        private double _indicatorCurrentX = 0;

        private void MoveIndicatorTo(FrameworkElement target, bool animate = true)
        {
            try
            {
                if (target == null || ViewSwitcherRoot == null || Indicator == null || IndicatorTranslate == null)
                    return;
                if (target.ActualWidth <= 0 || ViewSwitcherRoot.ActualWidth <= 0 || !target.IsLoaded || !ViewSwitcherRoot.IsLoaded)
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
            catch
            {
            }
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
            try
            {
                // 彻底断开绑定，防止虚拟化容器在切换过程中访问旧集合
                List.ItemsSource = null;
                List.ItemsPanel = null;
                List.ItemTemplate = null;
                List.ItemContainerStyle = null;

                // 设置模板
                if (Resources["ListItemsPanel"] is ItemsPanelTemplate ipt)
                    List.ItemsPanel = ipt;
                if (Resources["ListItemTemplate"] is DataTemplate dtemp)
                    List.ItemTemplate = dtemp;
                List.ItemContainerStyle = (Style)Application.Current.Resources["DefaultListViewItemStyle"];
                List.HorizontalContentAlignment = HorizontalAlignment.Stretch;

                // 恢复数据源
                List.ItemsSource = Favorites;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ApplyListView] " + ex);
                List.ItemsSource = Favorites;
            }
        }

        private void ApplyTileView()
        {
            try
            {
                List.ItemsSource = null;
                List.ItemsPanel = null;
                List.ItemTemplate = null;
                List.ItemContainerStyle = null;

                if (Resources["TileItemsPanel"] is ItemsPanelTemplate tip)
                    List.ItemsPanel = tip;
                if (Resources["TileItemTemplate"] is DataTemplate ttemp)
                    List.ItemTemplate = ttemp;
                if (Resources["TileListViewItemStyle"] is Style ts)
                    List.ItemContainerStyle = ts;
                else
                    List.ItemContainerStyle = (Style)Application.Current.Resources["DefaultListViewItemStyle"];

                List.ItemsSource = Favorites;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ApplyTileView] " + ex);
                List.ItemsSource = Favorites;
            }
        }

        public void ApplySearchFilter(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                List.ItemsSource = Favorites;
            }
            else
            {
                var filtered = Favorites.Where(f => !string.IsNullOrEmpty(f.Term) && f.Term.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
                List.ItemsSource = new ObservableCollection<FavoriteItem>(filtered);
            }
        }

        public void OnFavoritesSearchTextChanged(string text) => ApplySearchFilter(text);
        public void OnFavoritesSearchQuerySubmitted(string text) => ApplySearchFilter(text);
    }
}
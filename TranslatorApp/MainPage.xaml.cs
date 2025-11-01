// MainPage.xaml.cs
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.Core;
using Windows.ApplicationModel.Resources;

namespace TranslatorApp
{
    public enum InfoBarSeverity { Informational, Success, Warning, Error }

    public sealed partial class MainPage : Page
    {
        private DispatcherTimer _infoTimer;
        private ResourceLoader _loader = ResourceLoader.GetForCurrentView();

        public MainPage()
        {
            InitializeComponent();
            _infoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
            _infoTimer.Tick += (_, __) => HideToast();

            // 将窗口内容扩展到标题栏并设置自定义 TitleBar 区域为系统可拖动的标题栏（类似微软商店）
            try
            {
                var coreTitleBar = CoreApplication.GetCurrentView().TitleBar;
                coreTitleBar.ExtendViewIntoTitleBar = true;
                Window.Current.SetTitleBar(TitleBar);
            }
            catch
            {
                // 在某些主机/平台上该调用可能不可用，忽略异常以保持兼容
            }

            // 本地化文本
            AppDisplayNameText.Text = _loader.GetString("AppDisplayName");
            GlobalSearchBox.PlaceholderText = _loader.GetString("GlobalSearchBox");

            // 默认导航到查词翻译页（仅在初始未导航时）
            if (ContentFrame.Content == null)
            {
                ContentFrame.Navigate(typeof(Pages.LookupPage));
                NavView.SelectedItem = NavView.MenuItems.OfType<Microsoft.UI.Xaml.Controls.NavigationViewItem>().First();
            }
        }

        private void NavigateTo(string tag)
        {
            // 防重导航
            var target = tag switch
            {
                "Lookup" => typeof(Pages.LookupPage),
                "Translate" => typeof(Pages.TranslatePage),
                "Favorites" => typeof(Pages.FavoritesPage),
                "Settings" => typeof(Pages.SettingsPage),
                _ => typeof(Pages.LookupPage)
            };
            if (ContentFrame.CurrentSourcePageType != target)
            {
                ContentFrame.Navigate(target);
                UpdateSearchVisibility(tag);
            }
        }

        private void UpdateSearchVisibility(string tag)
        {
            //需要显示搜索框的页面：Lookup、Favorites（标题栏搜索）
            bool showSearch = tag == "Lookup" || tag == "Favorites";
            GlobalSearchBox.Visibility = showSearch ? Visibility.Visible : Visibility.Collapsed;
            SearchEngineCombo.Visibility = tag == "Lookup" ? Visibility.Visible : Visibility.Collapsed;
            ClearSearchButton.Visibility = Visibility.Collapsed;
        }

        private void NavView_SelectionChanged(Microsoft.UI.Xaml.Controls.NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                NavigateTo("Settings");
            }
            else
            {
                var item = (Microsoft.UI.Xaml.Controls.NavigationViewItem)args.SelectedItem;
                NavigateTo(item.Tag?.ToString());
            }
        }

        private void NavView_BackRequested(Microsoft.UI.Xaml.Controls.NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewBackRequestedEventArgs args)
        {
            if (ContentFrame.CanGoBack)
            {
                ContentFrame.GoBack();
            }
        }

        private void GoBackButton_Click(object sender, RoutedEventArgs e)
        {
            if (ContentFrame.CanGoBack)
            {
                ContentFrame.GoBack();
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            // Select the Settings item to navigate
            NavView.SelectedItem = NavView.SettingsItem;
        }

        private void ContentFrame_Navigated(object sender, Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            NavView.IsBackEnabled = ContentFrame.CanGoBack;

            // 控制搜索框显示/隐藏
            if (e.SourcePageType == typeof(Pages.LookupPage) || e.SourcePageType == typeof(Pages.FavoritesPage))
                GlobalSearchBox.Visibility = Visibility.Visible;
            else
                GlobalSearchBox.Visibility = Visibility.Collapsed;

            if (e.SourcePageType == typeof(Pages.SettingsPage))
            {
                NavView.SelectedItem = NavView.SettingsItem;
            }
            else
            {
                // 根据页面类型选中对应菜单项
                foreach (var mi in NavView.MenuItems.OfType<Microsoft.UI.Xaml.Controls.NavigationViewItem>())
                {
                    if (mi.Tag?.ToString() == (e.SourcePageType.Name.Replace("Page", "")))
                    {
                        NavView.SelectedItem = mi;
                        break;
                    }
                }
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) { /* UWP 无窗口 API，留空或调用AppWindow扩展（XAML Island另议） */ }
        private void Maximize_Click(object sender, RoutedEventArgs e) { }
        private void Close_Click(object sender, RoutedEventArgs e) { }

        // 搜索框联想：广播到当前页面（LookupPage 或 FavoritesPage）
        private void GlobalSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            ClearSearchButton.Visibility = string.IsNullOrWhiteSpace(sender.Text) ? Visibility.Collapsed : Visibility.Visible;
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                //让当前页处理联想
                if (ContentFrame.Content is Pages.ILiveSearch consumer)
                {
                    consumer.OnSearchSuggestion(sender.Text);
                }
            }
        }

        private void GlobalSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (ContentFrame.Content is Pages.ILiveSearch consumer)
            {
                consumer.OnSearchSubmitted(args.QueryText, SearchEngineCombo.SelectedIndex);
            }
        }

        private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
        {
            GlobalSearchBox.Text = string.Empty;
            ClearSearchButton.Visibility = Visibility.Collapsed;
            if (ContentFrame.Content is Pages.ILiveSearch consumer)
            {
                consumer.OnSearchCleared();
            }
        }

        public void ShowToast(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
        {
            // Use DispatcherQueue.GetForCurrentThread to enqueue
            DispatcherQueue.GetForCurrentThread().TryEnqueue(async () =>
            {
                if (TopInfoBar == null) return;

                if (TopInfoBar.IsOpen)
                {
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                    var slideOut = new DoubleAnimation
                    {
                        To = -Math.Max(80, TopInfoBar.ActualHeight +4),
                        Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                    };

                    var sbOut = new Storyboard();
                    sbOut.Children.Add(slideOut);
                    Storyboard.SetTarget(slideOut, TopInfoBar.RenderTransform);
                    Storyboard.SetTargetProperty(slideOut, "Y");
                    sbOut.Completed += (_, __) =>
                    {
                        try { TopInfoBar.IsOpen = false; } catch { }
                        tcs.TrySetResult(true);
                    };

                    try { sbOut.Begin(); } catch { tcs.TrySetResult(true); }

                    await tcs.Task;
                    await Task.Delay(50);
                }

                TopInfoBar.Severity = severity switch
                {
                    InfoBarSeverity.Informational => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
                    InfoBarSeverity.Success => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success,
                    InfoBarSeverity.Warning => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
                    InfoBarSeverity.Error => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error,
                    _ => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational
                };
                TopInfoBar.Message = message;
                try { TopInfoBar.IsOpen = true; } catch { }

                var slideIn = new DoubleAnimation
                {
                    To =0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                var sbIn = new Storyboard();
                sbIn.Children.Add(slideIn);
                Storyboard.SetTarget(slideIn, TopInfoBar.RenderTransform);
                Storyboard.SetTargetProperty(slideIn, "Y");
                try { sbIn.Begin(); } catch { }

                _infoTimer?.Stop();
                _infoTimer?.Start();
            });
        }

        public void HideToast()
        {
            DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
            {
                if (TopInfoBar == null || !TopInfoBar.IsOpen) return;

                var slideOut = new DoubleAnimation
                {
                    To = -Math.Max(80, TopInfoBar.ActualHeight +4),
                    Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };
                var sb = new Storyboard();
                sb.Children.Add(slideOut);
                Storyboard.SetTarget(slideOut, TopInfoBar.RenderTransform);
                Storyboard.SetTargetProperty(slideOut, "Y");
                sb.Completed += (_, __) =>
                {
                    try { TopInfoBar.IsOpen = false; } catch { }
                };
                try { sb.Begin(); } catch { TopInfoBar.IsOpen = false; }
            });
        }

        public void ShowSuccess(string message) => ShowToast(message, InfoBarSeverity.Success);
        public void ShowWarning(string message) => ShowToast(message, InfoBarSeverity.Warning);
        public void ShowError(string message) => ShowToast(message, InfoBarSeverity.Error);
        public void ShowInfo(string message) => ShowToast(message, InfoBarSeverity.Informational);

        private void SearchEngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ContentFrame.Content is Pages.ILiveSearch consumer)
            {
                consumer.OnSearchEngineChanged(SearchEngineCombo.SelectedIndex);
            }
        }
    }
}
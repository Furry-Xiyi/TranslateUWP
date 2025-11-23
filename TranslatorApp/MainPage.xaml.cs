using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using Windows.Foundation;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using muxc = Microsoft.UI.Xaml.Controls;
using TranslatorApp.Services;

namespace TranslatorApp
{
    public sealed partial class MainPage : Page
    {
        public static MainPage Current { get; private set; }
        private CoreApplicationViewTitleBar _coreTitleBar;
        public ComboBox LookupSiteComboBoxRef => LookupSiteComboBox;
        public AutoSuggestBox LookupSearchBoxRef => LookupSearchBox;
        public Button LookupCloseButtonRef => LookupCloseButton;
        public bool IsInLookupMode { get; set; }
        private readonly DispatcherTimer _infoTimer;

        // 窗口大小阈值
        private const double CompactModeThreshold = 1000;
        private const double MinWindowWidth = 900;
        private const double MinWindowHeight = 680;

        public MainPage()
        {
            InitializeComponent();
            Current = this;

            _infoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _infoTimer.Tick += (_, __) => { HideToast(); _infoTimer.Stop(); };

            this.Loaded += MainPage_Loaded;
            ContentFrame.Navigated += ContentFrame_Navigated;

            // 监听窗口大小变化
            Window.Current.SizeChanged += Window_SizeChanged;
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            InitializeTitleBar();
            TryLoadAppIcon();

            // 设置最小窗口大小
            SetMinimumWindowSize();

            try
            {
                var startItem = NavView.MenuItems
                    .OfType<muxc.NavigationViewItem>()
                    .FirstOrDefault(x => (string)x.Tag == "WordLookupPage");

                if (startItem != null)
                {
                    NavView.SelectedItem = startItem;
                }
            }
            catch { }

            NavigateTo(typeof(Pages.WordLookupPage));

            // 修复：恢复启动时的 API Key 检测逻辑
            CheckApiSetupOnStartup();

            // 初始化侧栏显示模式
            UpdateNavigationViewDisplayMode();
        }

        // 设置最小窗口大小
        private void SetMinimumWindowSize()
        {
            try
            {
                ApplicationView.GetForCurrentView().SetPreferredMinSize(
                    new Size(MinWindowWidth, MinWindowHeight)
                );
            }
            catch { }
        }

        // 窗口大小变化处理
        private void Window_SizeChanged(object sender, Windows.UI.Core.WindowSizeChangedEventArgs e)
        {
            UpdateNavigationViewDisplayMode();
        }

        // 根据窗口宽度自动切换侧栏显示模式
        private void UpdateNavigationViewDisplayMode()
        {
            var windowWidth = Window.Current.Bounds.Width;

            if (windowWidth < CompactModeThreshold)
            {
                // 窗口较小时，使用紧凑模式（自动折叠）
                NavView.PaneDisplayMode = muxc.NavigationViewPaneDisplayMode.LeftCompact;
                NavView.IsPaneOpen = false;
            }
            else
            {
                // 窗口较大时，使用左侧模式（自动展开）
                NavView.PaneDisplayMode = muxc.NavigationViewPaneDisplayMode.Left;
                NavView.IsPaneOpen = true;
            }
        }

        // ========== 新增/修复 API 检测逻辑 ==========
        private async void CheckApiSetupOnStartup()
        {
            // 1. 等待 UI 线程完全就绪，确保 XamlRoot 可用
            await Task.Delay(1000);

            // 2. 检查用户是否勾选了"不再提示"
            var ignore = Windows.Storage.ApplicationData.Current.LocalSettings.Values["IgnoreApiDialog"] as bool? ?? false;
            if (ignore) return;

            // 3. 检查所有 Key 是否为空
            bool hasBing = !string.IsNullOrEmpty(SettingsService.BingAppId);
            bool hasBaidu = !string.IsNullOrEmpty(SettingsService.BaiduAppId);
            bool hasYoudao = !string.IsNullOrEmpty(SettingsService.YoudaoAppKey);

            // 4. 如果所有 Key 都没有配置，才弹窗
            if (!hasBing && !hasBaidu && !hasYoudao)
            {
                ShowApiHelpDialog(showDoNotRemind: true);
            }
        }

        public async void ShowApiHelpDialog(bool showDoNotRemind)
        {
            try
            {
                var template = RootGrid.Resources["WelcomeApiExpanderTemplate"] as DataTemplate;
                var dialog = new ContentDialog
                {
                    XamlRoot = this.Content.XamlRoot,
                    Title = "配置翻译服务",
                    Content = template.LoadContent(),
                    PrimaryButtonText = "前往设置",
                    DefaultButton = ContentDialogButton.Primary,
                    CloseButtonText = "稍后配置"
                };

                dialog.HorizontalAlignment = HorizontalAlignment.Stretch;
                dialog.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                dialog.PrimaryButtonClick += (s, e) => { NavigateTo(typeof(Pages.SettingsPage)); };

                if (showDoNotRemind)
                {
                    dialog.SecondaryButtonText = "不再提示";
                    dialog.SecondaryButtonClick += (s, e) =>
                    {
                        Windows.Storage.ApplicationData.Current.LocalSettings.Values["IgnoreApiDialog"] = true;
                    };
                }

                await dialog.ShowAsync();
            }
            catch { }
        }
        // ==========================================

        private void InitializeTitleBar()
        {
            _coreTitleBar = CoreApplication.GetCurrentView().TitleBar;
            _coreTitleBar.ExtendViewIntoTitleBar = true;

            // 只将底层拖拽区设为标题栏
            Window.Current.SetTitleBar(AppTitleBar);

            var titleBar = ApplicationView.GetForCurrentView().TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

            // 监听布局变化
            _coreTitleBar.LayoutMetricsChanged += (s, e) => UpdateTitleBarLayout();
            _coreTitleBar.IsVisibleChanged += (s, e) => UpdateTitleBarLayout();

            UpdateTitleBarLayout();
        }

        private void UpdateTitleBarLayout()
        {
            if (_coreTitleBar == null) return;

            // 只调整列宽即可，系统会自动处理
            LeftPaddingColumn.Width = new GridLength(_coreTitleBar.SystemOverlayLeftInset);
            RightPaddingColumn.Width = new GridLength(_coreTitleBar.SystemOverlayRightInset);
        }

        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            NavView.IsBackEnabled = ContentFrame.CanGoBack;

            if (e.SourcePageType == typeof(Pages.SettingsPage))
            {
                NavView.SelectedItem = NavView.SettingsItem;
            }
            else
            {
                var item = NavView.MenuItems
                    .OfType<muxc.NavigationViewItem>()
                    .FirstOrDefault(x => (string)x.Tag == e.SourcePageType.Name);
                if (item != null) NavView.SelectedItem = item;
            }

            UpdateTitleBarForPage(e.SourcePageType);
        }

        private void UpdateTitleBarForPage(Type pageType)
        {
            if (pageType == typeof(Pages.WordLookupPage))
            {
                TitleBarCenterControl.Visibility = Visibility.Visible;
                LookupSiteComboBox.Visibility = Visibility.Visible;
                LookupSearchBox.PlaceholderText = "输入要查的词汇...";

                var lastSite = Services.SettingsService.LastLookupSite;
                foreach (ComboBoxItem item in LookupSiteComboBox.Items)
                {
                    if ((item.Tag?.ToString()) == lastSite) { LookupSiteComboBox.SelectedItem = item; break; }
                }
                if (LookupSiteComboBox.SelectedItem == null) LookupSiteComboBox.SelectedIndex = 2;
            }
            else if (pageType == typeof(Pages.FavoritesPage))
            {
                TitleBarCenterControl.Visibility = Visibility.Visible;
                LookupSiteComboBox.Visibility = Visibility.Collapsed;
                LookupSearchBox.PlaceholderText = "搜索收藏...";
                LookupCloseButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                TitleBarCenterControl.Visibility = Visibility.Collapsed;
            }

            LookupSearchBox.Text = string.Empty;
        }

        #region Title Bar Interactions

        private void LookupSite_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ContentFrame.Content is Pages.WordLookupPage page)
            {
                var site = (LookupSiteComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                if (!string.IsNullOrEmpty(site)) Services.SettingsService.LastLookupSite = site;

                if (!string.IsNullOrWhiteSpace(LookupSearchBox.Text))
                {
                    page.NavigateToSite(LookupSearchBox.Text);
                }
            }
        }

        private void LookupSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var text = sender.Text ?? "";

            if (ContentFrame.Content is Pages.WordLookupPage)
            {
                if (string.IsNullOrWhiteSpace(text)) { sender.ItemsSource = null; return; }
                var hist = Services.SettingsService.LookupHistory ?? new List<string>();
                var suggestions = hist.Where(h => h.StartsWith(text, StringComparison.OrdinalIgnoreCase)).Take(8).ToList();
                sender.ItemsSource = suggestions.Count > 0 ? suggestions : null;
            }
            else if (ContentFrame.Content is Pages.FavoritesPage favPage)
            {
                sender.ItemsSource = null;
                favPage.OnFavoritesSearchTextChanged(text);
            }
        }

        private void LookupSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            var q = args.QueryText?.Trim();
            if (string.IsNullOrWhiteSpace(q)) return;

            if (ContentFrame.Content is Pages.WordLookupPage wp)
            {
                wp.AddToHistory(q);
                wp.NavigateToSite(q);
            }
            else if (ContentFrame.Content is Pages.FavoritesPage fav)
            {
                fav.OnFavoritesSearchQuerySubmitted(q);
            }
        }

        private void LookupCloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (ContentFrame.Content is Pages.WordLookupPage wp)
            {
                wp.ReturnToDailySentence();
            }
            LookupCloseButton.Visibility = Visibility.Collapsed;
        }

        public void ShowCloseButton()
        {
            LookupCloseButton.Visibility = Visibility.Visible;
            IsInLookupMode = true;
        }

        #endregion

        #region Account Logic

        private void AccountButton_Click(object sender, RoutedEventArgs e)
        {
            FlyoutBase.ShowAttachedFlyout(AccountButton);
        }

        public void UpdateAccountUI(string displayName, ImageSource photo = null, string email = null)
        {
            bool signedIn = !string.IsNullOrEmpty(displayName);

            if (photo != null) AccountPicture.ProfilePicture = photo;
            else AccountPicture.Initials = string.IsNullOrEmpty(displayName) ? "?" : displayName.Substring(0, 1);

            LoginMenuItem.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
            SwitchAccountMenuItem.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
            LogoutMenuItem.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;

            FlyoutName.Text = string.IsNullOrEmpty(displayName) ? "未登录" : displayName;
            FlyoutEmail.Text = email ?? "";

            if (photo != null) FlyoutAvatar.ProfilePicture = photo;
            else FlyoutAvatar.Initials = string.IsNullOrEmpty(displayName) ? "?" : displayName.Substring(0, 1);
        }

        private async void LoginMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await ((App)Application.Current).InitMicrosoftAccountAsync();
        }

        private async void SwitchAccountMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await ((App)Application.Current).SignOutAsync();
            await ((App)Application.Current).InitMicrosoftAccountAsync();
        }

        private async void LogoutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await ((App)Application.Current).SignOutAsync();
            UpdateAccountUI(null, null);
        }

        private void SettingsItem_Click(object sender, RoutedEventArgs e)
        {
            NavigateTo(typeof(Pages.SettingsPage));
        }

        #endregion

        #region Toast / Helpers

        public void ShowInfo(string msg) => ShowToast(msg, InfoBarSeverity.Informational);
        public void ShowSuccess(string msg) => ShowToast(msg, InfoBarSeverity.Success);
        public void ShowError(string msg) => ShowToast(msg, InfoBarSeverity.Error);
        public void ShowWarning(string msg) => ShowToast(msg, InfoBarSeverity.Warning);

        private async void ShowToast(string message, InfoBarSeverity severity)
        {
            TopInfoBar.Message = message;
            TopInfoBar.Severity = severity;
            TopInfoBar.IsOpen = true;

            var trans = TopInfoBar.RenderTransform as TranslateTransform;
            if (trans != null)
            {
                var anim = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(200), EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut } };
                Storyboard.SetTarget(anim, trans);
                Storyboard.SetTargetProperty(anim, "Y");
                var sb = new Storyboard();
                sb.Children.Add(anim);
                sb.Begin();
            }

            _infoTimer.Start();
            await Task.CompletedTask;
        }

        private void HideToast()
        {
            TopInfoBar.IsOpen = false;
            var trans = TopInfoBar.RenderTransform as TranslateTransform;
            if (trans != null) trans.Y = -100;
        }

        private void TryLoadAppIcon()
        {
            try { AppIconImage.Source = new BitmapImage(Package.Current.Logo); } catch { }
        }

        private void NavigateTo(Type pageType, object param = null)
        {
            if (ContentFrame.CurrentSourcePageType != pageType)
                ContentFrame.Navigate(pageType, param);
        }

        private void NavView_SelectionChanged(muxc.NavigationView sender, muxc.NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected) NavigateTo(typeof(Pages.SettingsPage));
            else if (args.SelectedItem is muxc.NavigationViewItem item && item.Tag is string tag)
            {
                switch (tag)
                {
                    case "WordLookupPage": NavigateTo(typeof(Pages.WordLookupPage)); break;
                    case "OnlineTranslatePage": NavigateTo(typeof(Pages.OnlineTranslatePage)); break;
                    case "FavoritesPage": NavigateTo(typeof(Pages.FavoritesPage)); break;
                }
            }
        }

        private void NavView_BackRequested(muxc.NavigationView sender, muxc.NavigationViewBackRequestedEventArgs args)
        {
            if (ContentFrame.CanGoBack) ContentFrame.GoBack();
        }

        #endregion
    }
}
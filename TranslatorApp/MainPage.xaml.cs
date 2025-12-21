using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.ComponentModel;
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
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using muxc = Microsoft.UI.Xaml.Controls;
using TranslatorApp.Services;

namespace TranslatorApp
{
    // Converter for inverting bool to visibility
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool boolValue)
                return boolValue ? Visibility.Collapsed : Visibility.Visible;
            if (value is Visibility visibility)
                return visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public sealed partial class MainPage : Page, INotifyPropertyChanged
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

        // 账户信息缓存
        private string _cachedDisplayName = "";
        private string _cachedEmail = "";
        private ImageSource _cachedAvatar = null;

        // 登录状态属性（用于绑定）
        private bool _isRestoringLogin = false;
        public bool IsRestoringLogin
        {
            get => _isRestoringLogin;
            set
            {
                if (_isRestoringLogin != value)
                {
                    _isRestoringLogin = value;
                    OnPropertyChanged(nameof(IsRestoringLogin));
                    OnPropertyChanged(nameof(IsAccountButtonEnabled));
                    OnPropertyChanged(nameof(AccountLoadingRingVisibility));
                }
            }
        }

        public bool IsAccountButtonEnabled => !IsRestoringLogin;

        public Visibility AccountLoadingRingVisibility => IsRestoringLogin ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

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

            // 🔴 冷启动必须显式导航首页
            NavigateTo(typeof(Pages.WordLookupPage));

            // 同步侧栏选中状态（仅 UI 表现）
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

            // 启动时检查 API Key
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
            await Dispatcher.RunAsync(
                Windows.UI.Core.CoreDispatcherPriority.Low,
                async () =>
                {
                    await Task.Delay(200); // 给首帧一点时间

                    if (ShouldShowApiDialog())
                    {
                        await ShowApiHelpDialogAsync(showDoNotRemind: true);
                    }
                });
        }
        private bool ShouldShowApiDialog()
        {
            // 是否用户选择过"不再提示"
            var ignore = Windows.Storage.ApplicationData.Current.LocalSettings.Values["IgnoreApiDialog"] as bool?;
            if (ignore == true)
                return false;

            // 是否至少配置了一个 API
            bool hasBing = !string.IsNullOrEmpty(SettingsService.BingAppId);
            bool hasBaidu = !string.IsNullOrEmpty(SettingsService.BaiduAppId);
            bool hasYoudao = !string.IsNullOrEmpty(SettingsService.YoudaoAppKey);

            // 只有「一个都没配」才弹
            return !hasBing && !hasBaidu && !hasYoudao;
        }


        public async Task ShowApiHelpDialogAsync(bool showDoNotRemind)
        {
            try
            {
                var template = RootGrid.Resources["WelcomeApiExpanderTemplate"] as DataTemplate;

                var dialog = new ContentDialog
                {
                    XamlRoot = this.Content.XamlRoot,
                    Title = "配置翻译服务",
                    Content = template?.LoadContent(),
                    PrimaryButtonText = "前往设置",
                    DefaultButton = ContentDialogButton.Primary,
                    CloseButtonText = "稍后配置"
                };

                dialog.PrimaryButtonClick += (s, e) =>
                {
                    NavigateTo(typeof(Pages.SettingsPage));
                };

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
            catch
            {
                // 冷启动下被系统 cancel 是正常情况，必须吃掉
            }
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

        private void AccountFlyout_Opening(object sender, object e)
        {
            UpdateFlyoutAccountInfo();
        }
        private void FlyoutHeaderGrid_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement grid)
            {
                // 直接从 Grid 开始往下找，肯定能找到
                UpdateFlyoutContentFromRoot(grid);
            }
        }

        private void UpdateFlyoutAccountInfo()
        {
            if (AccountFlyout?.Items?.Count > 0 && AccountFlyout.Items[0] is MenuFlyoutItem headerItem)
            {
                try { headerItem.ApplyTemplate(); } catch { }

                // 尝试从 Item 更新。
                // 注意：如果 UI 还没渲染，这里会因为找不到子元素而“什么都不做”，
                // 但是没关系，上面的 FlyoutHeaderGrid_Loaded 随后会补刀。
                UpdateFlyoutContentFromRoot(headerItem);
            }
        }
        private void UpdateFlyoutContentFromRoot(DependencyObject root)
        {
            var flyoutNameElem = FindElementByName(root, "FlyoutName") as TextBlock;
            var flyoutEmailElem = FindElementByName(root, "FlyoutEmail") as TextBlock;
            var flyoutAvatarElem = FindElementByName(root, "FlyoutAvatar") as FrameworkElement;

            // 更新文本
            if (flyoutNameElem != null)
                flyoutNameElem.Text = string.IsNullOrEmpty(_cachedDisplayName) ? "未登录" : _cachedDisplayName;

            if (flyoutEmailElem != null)
                flyoutEmailElem.Text = _cachedEmail ?? "";

            // 更新头像
            if (flyoutAvatarElem != null)
            {
                var avatarType = flyoutAvatarElem.GetType();

                // 设置图片 (ProfilePicture)
                var propProfile = avatarType.GetProperty("ProfilePicture");
                if (propProfile != null && propProfile.CanWrite)
                {
                    propProfile.SetValue(flyoutAvatarElem, _cachedAvatar);
                }

                // 设置首字母 (Initials)，防止图片为空时显示残留信息
                if (_cachedAvatar == null)
                {
                    var propInitials = avatarType.GetProperty("Initials");
                    if (propInitials != null && propInitials.CanWrite)
                    {
                        string initials = string.IsNullOrEmpty(_cachedDisplayName) ? "" : _cachedDisplayName.Substring(0, 1);
                        propInitials.SetValue(flyoutAvatarElem, initials);
                    }
                }
            }
        }

        //递归查找元素
        private FrameworkElement FindElementByName(DependencyObject root, string name)
        {
            if (root == null) return null;

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement fe)
                {
                    if (fe.Name == name) return fe;
                    var found = FindElementByName(child, name);
                    if (found != null) return found;
                }
                else
                {
                    var found = FindElementByName(child, name);
                    if (found != null) return found;
                }
            }
            return null;
        }

        public void UpdateAccountUI(string displayName, ImageSource photo = null, string email = null)
        {
            // 缓存账户信息
            _cachedDisplayName = displayName ?? "";
            _cachedEmail = email ?? "";
            _cachedAvatar = photo;

            bool signedIn = !string.IsNullOrEmpty(displayName);

            // ===== 主界面头像 =====
            // 关键修复：显式清空或设置图片
            AccountPicture.ProfilePicture = photo;

            if (photo == null)
            {
                // 如果没有图片，且未登录，设置为空字符串以显示默认人像图标
                // 之前是 set "?" 导致显示问号
                AccountPicture.Initials = signedIn ? (displayName.Substring(0, 1)) : "";
            }

            // ===== 菜单项可见性 =====
            LoginMenuItem.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
            SwitchAccountMenuItem.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
            LogoutMenuItem.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
            SettingsMenuItem.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;

            // 如果 Flyout 此时是打开的，强制刷新一下 Flyout 内容
            if (AccountButton.Flyout != null && AccountButton.Flyout.IsOpen)
            {
                UpdateFlyoutAccountInfo();
            }
        }

        public void StartRestoringLogin()
        {
            IsRestoringLogin = true;
        }

        // 完成恢复登录（清除 loading 状态）
        public void FinishRestoringLogin()
        {
            IsRestoringLogin = false;
        }

        private async void LoginMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StartRestoringLogin(); // 手动开启 Loading
                await ((App)Application.Current).InitMicrosoftAccountAsync();
            }
            finally
            {
                FinishRestoringLogin(); // 无论成功失败都关闭 Loading
            }
        }

        private async void SwitchAccountMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StartRestoringLogin(); // 手动开启 Loading
                await ((App)Application.Current).SignOutAsync();
                await ((App)Application.Current).InitMicrosoftAccountAsync();
            }
            finally
            {
                FinishRestoringLogin(); // 关闭 Loading
            }
        }

        private async void LogoutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await ((App)Application.Current).SignOutAsync();

            // 这会触发 UpdateAccountUI，进入上面写好的逻辑，清空图片和文字
            UpdateAccountUI(null, null);

            Windows.Storage.ApplicationData.Current.LocalSettings.Values["HasEverLoggedIn"] = false;
        }

        private async void SettingsItem_Click(object sender, RoutedEventArgs e)
        {
            var uri = new Uri("https://account.microsoft.com/privacy/app-access");
            await Windows.System.Launcher.LaunchUriAsync(uri);
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
            if (pageType == typeof(Pages.WordLookupPage))
            {
                ContentFrame.Navigate(pageType, param,
                    new EntranceNavigationTransitionInfo());
                return;
            }

            if (ContentFrame.CurrentSourcePageType != pageType)
                ContentFrame.Navigate(pageType, param);
        }


        private void NavView_SelectionChanged(muxc.NavigationView sender, muxc.NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                NavigateTo(typeof(Pages.SettingsPage));
                FavIcon.Symbol = Symbol.OutlineStar;
            }
            else if (args.SelectedItem is muxc.NavigationViewItem item && item.Tag is string tag)
            {
                if (tag == "FavoritesPage")
                {
                    FavIcon.Symbol = Symbol.Favorite;
                }
                else
                {
                    FavIcon.Symbol = Symbol.OutlineStar;
                }
                // ------------------

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
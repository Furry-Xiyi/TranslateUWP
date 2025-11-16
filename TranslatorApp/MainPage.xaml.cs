using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Controls; // InfoBar, InfoBarSeverity, PersonPicture

namespace TranslatorApp
{
    public sealed partial class MainPage : Page
    {
        public static MainPage Current { get; private set; }
        private CoreApplicationViewTitleBar _coreTitleBar;

        public ComboBox LookupSiteComboBox { get; private set; }
        public AutoSuggestBox LookupSearchBox { get; private set; }
        public Button LookupCloseButton { get; private set; }

        public bool IsInLookupMode { get; set; }

        private readonly DispatcherTimer _infoTimer;

        private const string Key_WhatsNewShownVersion = "WhatsNewShownVersion";
        private const string Key_UpdateIgnoreVersion = "UpdateIgnoreVersion";
        private const bool DEBUG_SHOW_DRAG_REGION = true;

        public MainPage()
        {
            this.InitializeComponent();
            Current = this;

            ContentFrame.Navigated += ContentFrame_Navigated;
            NavView.SelectedItem = Nav_Online;
            NavigateTo(typeof(Pages.WordLookupPage));

            _infoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _infoTimer.Tick += (_, __) => { HideToast(); _infoTimer.Stop(); };

            Loaded += async (s, e) =>
            {
                TryLoadAppIcon();

                if (ShouldShowWhatsNewForCurrentVersion())
                {
                    var result = await ShowWhatsNewDialogAsync();
                    if (result == ContentDialogResult.Primary && !Services.SettingsService.HasAnyApiKey())
                        await ShowWelcomeDialogAsync();
                }
                else
                {
                    if (!Services.SettingsService.HasAnyApiKey())
                        await ShowWelcomeDialogAsync();
                }

                _ = CheckForUpdatesAsync();

                // 页面侧仅负责视觉高度与本地交互，不处理横向长度（交由 App）
                SetUpCustomTitleBar();

                // 通知 App 注册拖拽区，由 App 统一管理 inset/DPI/SizeChanged 刷新
                try
                {
                    ((App)Application.Current).RegisterWindowTitleBar(
                        DragRegion,
                        CustomDragRegion,
                        AccountButtonHost ?? (FrameworkElement)AccountPicture,
                        TitleBarCenterHost);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[MainPage] RegisterWindowTitleBar failed: " + ex);
                }

                UpdateLocalTitleVisuals();
            };

            SizeChanged += (s, e) => UpdateLocalTitleVisuals();
        }

        private void SetUpCustomTitleBar()
        {
            try
            {
                _coreTitleBar = CoreApplication.GetCurrentView().TitleBar;
                _coreTitleBar.ExtendViewIntoTitleBar = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SetUpCustomTitleBar] ExtendViewIntoTitleBar failed: " + ex);
            }

            try
            {
                if (DragRegion != null)
                    Window.Current.SetTitleBar(DragRegion);
                else if (CustomDragRegion != null)
                    Window.Current.SetTitleBar(CustomDragRegion);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SetTitleBar] " + ex);
            }

            try
            {
                var titleBar = ApplicationView.GetForCurrentView().TitleBar;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                titleBar.ButtonHoverBackgroundColor = Colors.Transparent;
                titleBar.ButtonPressedBackgroundColor = Colors.Transparent;

                bool isDark = (Window.Current.Content as FrameworkElement)?.RequestedTheme == ElementTheme.Dark;
                titleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
                titleBar.ButtonInactiveForegroundColor = isDark
                    ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0x99, 0x00, 0x00, 0x00);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[SetUpCustomTitleBar] TitleBar color sync failed: " + ex);
            }

            try
            {
                if (DragRegion != null) DragRegion.IsHitTestVisible = true;
                if (TitleBarCenterHost != null) Canvas.SetZIndex(TitleBarCenterHost, 1);
                if (DragRegion != null) Canvas.SetZIndex(DragRegion, 0);
                if (AccountButtonHost != null) Canvas.SetZIndex(AccountButtonHost, 2);
            }
            catch { }

            AddPointerHandlersToInteractiveControls();
        }

        // 页面侧视觉高度与调试显示（不改横向长度）
        private void UpdateLocalTitleVisuals()
        {
            try
            {
                if (DragRegion == null) return;

                try
                {
                    double sysCaption = _coreTitleBar?.Height ?? 0;
                    double desired = Math.Max(48.0, sysCaption);

                    DragRegion.Height = desired;
                    DragRegion.VerticalAlignment = VerticalAlignment.Top;

                    if (CustomDragRegion != null)
                        CustomDragRegion.Height = desired;

                    if (TitleBarCenterHost != null)
                    {
                        TitleBarCenterHost.Height = desired;
                        TitleBarCenterHost.VerticalAlignment = VerticalAlignment.Center;
                    }
                }
                catch { }

                if (DEBUG_SHOW_DRAG_REGION)
                    DragRegion.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0x00, 0x00));
                else
                    DragRegion.Background = new SolidColorBrush(Windows.UI.Colors.Transparent);
            }
            catch { }
        }

        private void AccountButtonHost_Click(object sender, RoutedEventArgs e)
        {
            try { AccountMenu?.ShowAt(AccountButtonHost); } catch { }
        }

        private async void LoginMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try { await ((TranslatorApp.App)Application.Current).InitMicrosoftAccountAsync(); } catch { }
            ((App)Application.Current).RefreshTitleBarNow();
        }

        private async void SwitchAccountMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await ((TranslatorApp.App)Application.Current).SignOutAsync();
                await ((TranslatorApp.App)Application.Current).InitMicrosoftAccountAsync();
            }
            catch { }
            ((App)Application.Current).RefreshTitleBarNow();
        }

        private async void LogoutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try { await ((TranslatorApp.App)Application.Current).SignOutAsync(); } catch { }
            UpdateAccountUI(null, null);
            ((App)Application.Current).RefreshTitleBarNow();
        }

        public void UpdateAccountUI(string displayName, ImageSource photo = null)
        {
            bool signedIn = !string.IsNullOrEmpty(displayName);

            try
            {
                LoginMenuItem.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
                SwitchAccountMenuItem.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
                LogoutMenuItem.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
            }
            catch
            {
                try
                {
                    if (AccountMenu != null)
                    {
                        foreach (var item in AccountMenu.Items.OfType<MenuFlyoutItem>())
                        {
                            if (string.Equals(item.Text, "登录", StringComparison.Ordinal)) item.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
                            if (string.Equals(item.Text, "切换账号", StringComparison.Ordinal)) item.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
                            if (string.Equals(item.Text, "注销", StringComparison.Ordinal)) item.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
                        }
                    }
                }
                catch { }
            }

            try
            {
                if (photo != null)
                {
                    AccountPicture.ProfilePicture = photo;
                    AccountPicture.Initials = null;
                }
                else
                {
                    AccountPicture.ProfilePicture = null;
                    AccountPicture.Initials = string.IsNullOrEmpty(displayName) ? "?" : displayName.Substring(0, 1);
                }

                AccountPicture.DisplayName = displayName;
            }
            catch { }
        }

        // InfoBar（保留原有动画逻辑）
        public async void ShowToast(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
        {
            await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
            {
                try
                {
                    if (TopInfoBar == null || TopInfoBarText == null) return;

                    if (TopInfoBar.IsOpen)
                    {
                        var tcs = new TaskCompletionSource<bool>();

                        if (!(TopInfoBar.RenderTransform is TranslateTransform outTrans))
                        {
                            outTrans = new TranslateTransform { Y = 0 };
                            TopInfoBar.RenderTransform = outTrans;
                        }

                        var slideOut = new DoubleAnimation
                        {
                            To = -80,
                            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
                            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                        };
                        Storyboard.SetTarget(slideOut, outTrans);
                        Storyboard.SetTargetProperty(slideOut, "Y");

                        var sbOut = new Storyboard();
                        sbOut.Children.Add(slideOut);
                        sbOut.Completed += (_, __) =>
                        {
                            TopInfoBar.IsOpen = false;
                            tcs.TrySetResult(true);
                        };
                        sbOut.Begin();

                        await tcs.Task;
                        await Task.Delay(50);
                    }

                    TopInfoBarText.Text = message;
                    TopInfoBar.Severity = severity;
                    TopInfoBar.IsOpen = true;

                    if (!(TopInfoBar.RenderTransform is TranslateTransform trans))
                    {
                        trans = new TranslateTransform { Y = -80 };
                        TopInfoBar.RenderTransform = trans;
                    }
                    else
                    {
                        trans.Y = -80;
                    }

                    var slideIn = new DoubleAnimation
                    {
                        To = 0,
                        Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    Storyboard.SetTarget(slideIn, trans);
                    Storyboard.SetTargetProperty(slideIn, "Y");

                    var sbIn = new Storyboard();
                    sbIn.Children.Add(slideIn);
                    sbIn.Begin();

                    _infoTimer?.Stop();
                    _infoTimer?.Start();
                }
                catch
                {
                    try
                    {
                        TopInfoBarText.Text = message;
                        TopInfoBar.Severity = severity;
                        TopInfoBar.IsOpen = true;
                        _infoTimer?.Stop();
                        _infoTimer?.Start();
                    }
                    catch { }
                }
            });
        }

        public void HideToast()
        {
            var _ = this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    if (TopInfoBar == null || !TopInfoBar.IsOpen) return;

                    if (!(TopInfoBar.RenderTransform is TranslateTransform trans))
                    {
                        trans = new TranslateTransform { Y = 0 };
                        TopInfoBar.RenderTransform = trans;
                    }

                    var slideOut = new DoubleAnimation
                    {
                        To = -80,
                        Duration = new Duration(TimeSpan.FromMilliseconds(180)),
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                    };
                    Storyboard.SetTarget(slideOut, trans);
                    Storyboard.SetTargetProperty(slideOut, "Y");

                    var sb = new Storyboard();
                    sb.Children.Add(slideOut);
                    sb.Completed += (_, __) => TopInfoBar.IsOpen = false;
                    sb.Begin();
                }
                catch
                {
                    try { TopInfoBar.IsOpen = false; } catch { }
                }
            });
        }

        public void ShowInfo(string msg) => ShowToast(msg, InfoBarSeverity.Informational);
        public void ShowSuccess(string msg) => ShowToast(msg, InfoBarSeverity.Success);
        public void ShowWarning(string msg) => ShowToast(msg, InfoBarSeverity.Warning);
        public void ShowError(string msg) => ShowToast(msg, InfoBarSeverity.Error);

        private void NavigateTo(Type pageType, object param = null)
        {
            if (ContentFrame.CurrentSourcePageType != pageType)
                ContentFrame.Navigate(pageType, param);
        }

        private void NavView_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                NavigateTo(typeof(Pages.SettingsPage));
                return;
            }

            if (args.SelectedItem is Microsoft.UI.Xaml.Controls.NavigationViewItem nvi)
            {
                switch (nvi.Tag as string)
                {
                    case "WordLookupPage":
                        NavigateTo(typeof(Pages.WordLookupPage));
                        break;
                    case "OnlineTranslatePage":
                        NavigateTo(typeof(Pages.OnlineTranslatePage));
                        break;
                    case "FavoritesPage":
                        NavigateTo(typeof(Pages.FavoritesPage));
                        break;
                    case "SettingsPage":
                        NavigateTo(typeof(Pages.SettingsPage));
                        break;
                }
            }
        }

        private void NavView_BackRequested(Microsoft.UI.Xaml.Controls.NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewBackRequestedEventArgs args)
        {
            if (ContentFrame.CanGoBack) ContentFrame.GoBack();
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
                var match = NavView.MenuItems
                    .OfType<Microsoft.UI.Xaml.Controls.NavigationViewItem>()
                    .FirstOrDefault(item => (string)item.Tag == e.SourcePageType.Name);
                if (match != null) NavView.SelectedItem = match;
            }

            if (LookupCloseButton != null) LookupCloseButton.Visibility = Visibility.Collapsed;
            IsInLookupMode = false;

            TryAttachTitleBarForCurrentPage();
        }

        private async Task ShowWelcomeDialogAsync()
        {
            var contentPanel = new StackPanel { Spacing = 12, Padding = new Windows.UI.Xaml.Thickness(12, 0, 12, 0) };
            contentPanel.Children.Add(new TextBlock { Text = "使用互译需要填写 API 密钥", Opacity = 0.8, TextWrapping = TextWrapping.Wrap });
            var template = (DataTemplate)RootGrid.Resources["WelcomeApiExpanderTemplate"];
            var expander = (FrameworkElement)template.LoadContent();
            contentPanel.Children.Add(expander);

            var dialog = new ContentDialog
            {
                Title = "欢迎使用翻译",
                PrimaryButtonText = "去填写",
                CloseButtonText = "稍后",
                Content = contentPanel
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                NavigateTo(typeof(Pages.SettingsPage));
                NavView.IsSettingsVisible = true;
                (NavView.SettingsItem as Control)?.Focus(FocusState.Programmatic);
            }

            ((App)Application.Current).RefreshTitleBarNow();
        }

        private bool ShouldShowWhatsNewForCurrentVersion()
        {
            var current = GetCurrentVersionString();
            var lastShown = ReadLocalSetting<string>(Key_WhatsNewShownVersion);
            return string.IsNullOrEmpty(lastShown) || !string.Equals(current, lastShown, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<ContentDialogResult> ShowWhatsNewDialogAsync(bool forceOpen = false)
        {
            string notes = await LoadWhatsNewTextAsync();
            var currentVersion = GetCurrentVersionString();
            var dialog = new ContentDialog
            {
                Title = $"更新内容（{currentVersion}）",
                PrimaryButtonText = "确定",
                Content = new ScrollViewer
                {
                    Content = new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap }
                }
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
                WriteLocalSetting(Key_WhatsNewShownVersion, currentVersion);

            ((App)Application.Current).RefreshTitleBarNow();
            return result;
        }

        private static string GetCurrentVersionString()
        {
            try
            {
                var v = Package.Current?.Id?.Version;
                if (v.HasValue)
                {
                    var ver = v.Value;
                    return $"{ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}";
                }
            }
            catch { }
            return "0.0.0.0";
        }

        private async Task CheckForUpdatesAsync() => await Task.CompletedTask;

        public void ShowCloseButton()
        {
            if (IsInLookupMode && LookupCloseButton != null)
                LookupCloseButton.Visibility = Visibility.Visible;
        }

        private Task<string> LoadWhatsNewTextAsync()
        {
            try
            {
                var loader = new Windows.ApplicationModel.Resources.ResourceLoader();
                string notes = loader.GetString("ReleaseNotes");
                if (!string.IsNullOrWhiteSpace(notes)) return Task.FromResult(notes);
                return Task.FromResult("暂无发行说明");
            }
            catch
            {
                return Task.FromResult("暂无发行说明");
            }
        }

        private void TryAttachTitleBarForCurrentPage()
        {
            TitleBarCenterHost.Children.Clear();

            if (ContentFrame.Content is Pages.WordLookupPage)
            {
                EnsureTitleBarControls();
            }
            else
            {
                TitleBarCenterHost.Visibility = Visibility.Collapsed;
            }

            ((App)Application.Current).RefreshTitleBarNow();
        }

        public void EnsureTitleBarControls()
        {
            if (TitleBarCenterHost.Children.Count > 0)
            {
                TitleBarCenterHost.Visibility = Visibility.Visible;
                ((App)Application.Current).RefreshTitleBarNow();
                return;
            }

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            LookupSiteComboBox = new ComboBox { Width = 140 };
            LookupSiteComboBox.Items.Add(new ComboBoxItem { Content = "Google", Tag = "Google" });
            LookupSiteComboBox.Items.Add(new ComboBoxItem { Content = "Bing", Tag = "Bing" });
            LookupSiteComboBox.Items.Add(new ComboBoxItem { Content = "Youdao", Tag = "Youdao" });

            var lastSite = Services.SettingsService.LastLookupSite;
            foreach (ComboBoxItem item in LookupSiteComboBox.Items)
            {
                if ((item.Tag?.ToString() ?? "") == lastSite)
                {
                    LookupSiteComboBox.SelectedItem = item;
                    break;
                }
            }
            if (LookupSiteComboBox.SelectedItem == null) LookupSiteComboBox.SelectedIndex = 2;

            LookupSearchBox = new AutoSuggestBox { Width = 600, PlaceholderText = "输入要查的词汇..." };
            LookupCloseButton = new Button { Content = "×", Width = 32, Height = 32, Visibility = Visibility.Collapsed };
            LookupCloseButton.Click += (_, __) =>
            {
                if (ContentFrame.Content is Pages.WordLookupPage page) page.ReturnToDailySentence();
                LookupCloseButton.Visibility = Visibility.Collapsed;
            };

            panel.Children.Add(LookupSiteComboBox);
            panel.Children.Add(LookupSearchBox);
            panel.Children.Add(LookupCloseButton);

            TitleBarCenterHost.Children.Add(panel);
            TitleBarCenterHost.Visibility = Visibility.Visible;

            LookupSiteComboBox.SelectionChanged += LookupSite_SelectionChanged;
            LookupSearchBox.QuerySubmitted += LookupSearchBox_QuerySubmitted;
            LookupSearchBox.TextChanged += LookupSearchBox_TextChanged;

            AddPointerHandlersToElement(LookupSearchBox);
            AddPointerHandlersToElement(LookupSiteComboBox);
            AddPointerHandlersToElement(LookupCloseButton);

            ((App)Application.Current).RefreshTitleBarNow();
        }

        private void LookupSite_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var site = (LookupSiteComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (!string.IsNullOrEmpty(site)) Services.SettingsService.LastLookupSite = site;

            var text = LookupSearchBox?.Text;
            if (!string.IsNullOrWhiteSpace(text) && ContentFrame.Content is Pages.WordLookupPage wp)
                wp.NavigateToSite(text);
        }

        private void LookupSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            var q = args.QueryText?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(q) && ContentFrame.Content is Pages.WordLookupPage wp)
            {
                wp.AddToHistory(q);
                wp.NavigateToSite(q);
            }
        }

        private void LookupSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

            var text = sender.Text ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                sender.ItemsSource = null;
                return;
            }

            var hist = Services.SettingsService.LookupHistory ?? new List<string>();
            var suggestions = hist.Where(h => h.StartsWith(text, StringComparison.OrdinalIgnoreCase)).Take(8).ToList();

            sender.ItemsSource = suggestions.Count > 0 ? suggestions : null;
        }

        private void TryLoadAppIcon()
        {
            try
            {
                var uri = Package.Current?.Logo;
                if (uri != null)
                {
                    AppIconImage.Source = new BitmapImage(uri);
                    return;
                }
            }
            catch { }
            AppIconImage.Source = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.png"));
        }

        private static T ReadLocalSetting<T>(string key)
        {
            var values = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
            if (values.TryGetValue(key, out var obj) && obj is T t) return t;
            return default;
        }

        private static void WriteLocalSetting(string key, object value)
        {
            Windows.Storage.ApplicationData.Current.LocalSettings.Values[key] = value;
        }

        private void AddPointerHandlersToElement(UIElement element)
        {
            if (element == null) return;
            element.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((s, e) => e.Handled = true), true);
            element.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((s, e) => e.Handled = true), true);
        }

        private void AddPointerHandlersToInteractiveControls()
        {
            AddPointerHandlersToElement(LookupSearchBox);
            AddPointerHandlersToElement(LookupSiteComboBox);
            AddPointerHandlersToElement(LookupCloseButton);
            if (AccountButtonHost != null) AddPointerHandlersToElement(AccountButtonHost);
            else if (AccountPicture != null) AddPointerHandlersToElement(AccountPicture);
        }
    }
}
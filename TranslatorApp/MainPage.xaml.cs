using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using muxc = Microsoft.UI.Xaml.Controls;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace TranslatorApp
{
    public sealed partial class MainPage : Page
    {
        public static MainPage? Current { get; private set; }

        public ComboBox? LookupSiteComboBox { get; private set; }
        public AutoSuggestBox? LookupSearchBox { get; private set; }
        public Button? LookupCloseButton { get; private set; }
        public AutoSuggestBox? FavoritesSearchBox { get; private set; }

        public bool IsInLookupMode { get; set; }

        private readonly DispatcherTimer _infoTimer;

        private const string Key_WhatsNewShownVersion = "WhatsNewShownVersion";
        private const string Key_UpdateIgnoreVersion = "UpdateIgnoreVersion";

        private const string GitHubOwner = "Furry-Xiyi";
        private const string GitHubRepo = "TranslatorApp";

        public MainPage()
        {
            this.InitializeComponent();
            Current = this;

            ContentFrame.Navigated += ContentFrame_Navigated;
            NavView.SelectedItem = Nav_WordLookup;
            NavigateTo(typeof(Pages.WordLookupPage));

            _infoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _infoTimer.Tick += (_, __) =>
            {
                HideToast();
                _infoTimer.Stop();
            };

            Loaded += async (s, e) =>
            {
                TryLoadAppIcon();

                if (ShouldShowWhatsNewForCurrentVersion())
                {
                    var result = await ShowWhatsNewDialogAsync();
                    if (result == ContentDialogResult.Primary && !Services.SettingsService.HasAnyApiKey())
                    {
                        await ShowWelcomeDialogAsync();
                    }
                }
                else
                {
                    if (!Services.SettingsService.HasAnyApiKey())
                        await ShowWelcomeDialogAsync();
                }

                _ = CheckForUpdatesAsync();
            };
        }

        private void AccountButton_Loaded(object sender, RoutedEventArgs e) { }

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

        public void UpdateAccountUI(string? displayName, ImageSource? photo = null)
        {
            bool signedIn = !string.IsNullOrEmpty(displayName);

            LoginMenuItem.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
            SwitchAccountMenuItem.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
            LogoutMenuItem.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;

            if (photo != null)
            {
                AccountImage.Source = photo;
                AccountImage.Visibility = Visibility.Visible;
                AccountPlaceholderIcon.Visibility = Visibility.Collapsed;
            }
            else
            {
                AccountImage.Source = null;
                AccountImage.Visibility = Visibility.Collapsed;
                AccountPlaceholderIcon.Visibility = Visibility.Visible;
            }
        }

        public void ShowToast(string message)
        {
            TopInfoBarText.Text = message;
            TopInfoBar.Visibility = Visibility.Visible;
            _infoTimer.Stop();
            _infoTimer.Start();
        }

        public void HideToast() => TopInfoBar.Visibility = Visibility.Collapsed;

        public void ShowInfo(string msg) => ShowToast(msg);
        public void ShowSuccess(string msg) => ShowToast(msg);
        public void ShowWarning(string msg) => ShowToast(msg);
        public void ShowError(string msg) => ShowToast(msg);

        private void NavigateTo(Type pageType, object? param = null)
        {
            if (ContentFrame.CurrentSourcePageType != pageType)
                ContentFrame.Navigate(pageType, param);
        }

        private void NavView_SelectionChanged(muxc.NavigationView sender, muxc.NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                NavigateTo(typeof(Pages.SettingsPage));
                return;
            }

            if (args.SelectedItem is muxc.NavigationViewItem nvi)
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

        private void NavView_BackRequested(muxc.NavigationView sender, muxc.NavigationViewBackRequestedEventArgs args)
        {
            if (ContentFrame.CanGoBack)
                ContentFrame.GoBack();
        }

        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            NavView.IsBackEnabled = ContentFrame.CanGoBack;

            if (e.SourcePageType == typeof(Pages.SettingsPage)) NavView.SelectedItem = NavView.SettingsItem;
            else
            {
                var match = NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => (string)item.Tag == e.SourcePageType.Name);
                if (match != null) NavView.SelectedItem = match;
            }

            if (LookupCloseButton != null) LookupCloseButton.Visibility = Visibility.Collapsed;
            IsInLookupMode = false;

            TryAttachTitleBarForCurrentPage();
        }

        private async Task ShowWelcomeDialogAsync()
        {
            var contentPanel = new StackPanel { Spacing = 12, Padding = new Thickness(12, 0, 12, 0) };
            contentPanel.Children.Add(new TextBlock { Text = "使用互译需要填写 API 密钥", Opacity = 0.8, TextWrapping = TextWrapping.Wrap });
            var template = (DataTemplate)RootGrid.Resources["WelcomeApiExpanderTemplate"];
            var expander = (FrameworkElement)template.LoadContent();
            contentPanel.Children.Add(expander);

            var dialog = new ContentDialog { Title = "欢迎使用翻译", PrimaryButtonText = "去填写", CloseButtonText = "稍后", Content = contentPanel };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary) { NavigateTo(typeof(Pages.SettingsPage)); NavView.IsSettingsVisible = true; (NavView.SettingsItem as Control)?.Focus(FocusState.Programmatic); }
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
            var dialog = new ContentDialog { Title = $"更新内容（{currentVersion}）", PrimaryButtonText = "确定", Content = new ScrollViewer { Content = new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap } } };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary) WriteLocalSetting(Key_WhatsNewShownVersion, currentVersion);
            return result;
        }
        // 如果缺少 GetCurrentVersionString 实现，添加一个安全实现
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

        // 简单占位的 CheckForUpdatesAsync（避免未定义引用报错）
        // 你可以把原来的网络检查逻辑恢复到这里
        private async Task CheckForUpdatesAsync()
        {
            await Task.CompletedTask;
        }

        // 在 MainPage 类中添加 ShowCloseButton（供 WordLookupPage 调用）
        public void ShowCloseButton()
        {
            if (IsInLookupMode && LookupCloseButton != null)
            {
                LookupCloseButton.Visibility = Visibility.Visible;
            }
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
            catch { return Task.FromResult("暂无发行说明"); }
        }

        private void TryAttachTitleBarForCurrentPage()
        {
            TitleBarCenterHost.Children.Clear();
            if (ContentFrame.Content is Pages.WordLookupPage) EnsureTitleBarControls();
            else TitleBarCenterHost.Visibility = Visibility.Collapsed;
        }

        public void EnsureTitleBarControls()
        {
            if (TitleBarCenterHost.Children.Count > 0) { TitleBarCenterHost.Visibility = Visibility.Visible; return; }

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

            LookupSiteComboBox = new ComboBox { Width = 140 };
            LookupSiteComboBox.Items.Add(new ComboBoxItem { Content = "Google", Tag = "Google" });
            LookupSiteComboBox.Items.Add(new ComboBoxItem { Content = "Bing", Tag = "Bing" });
            LookupSiteComboBox.Items.Add(new ComboBoxItem { Content = "Youdao", Tag = "Youdao" });

            var lastSite = Services.SettingsService.LastLookupSite;
            foreach (ComboBoxItem item in LookupSiteComboBox.Items)
            {
                if ((item.Tag?.ToString() ?? "") == lastSite) { LookupSiteComboBox.SelectedItem = item; break; }
            }
            if (LookupSiteComboBox.SelectedItem == null) LookupSiteComboBox.SelectedIndex = 2;

            LookupSearchBox = new AutoSuggestBox { Width = 600, PlaceholderText = "输入要查的词汇..." };
            LookupCloseButton = new Button { Content = "×", Width = 32, Height = 32, Visibility = Visibility.Collapsed };
            LookupCloseButton.Click += (_, __) => { if (ContentFrame.Content is Pages.WordLookupPage page) page.ReturnToDailySentence(); LookupCloseButton.Visibility = Visibility.Collapsed; };

            panel.Children.Add(LookupSiteComboBox);
            panel.Children.Add(LookupSearchBox);
            panel.Children.Add(LookupCloseButton);

            TitleBarCenterHost.Children.Add(panel);
            TitleBarCenterHost.Visibility = Visibility.Visible;

            LookupSiteComboBox.SelectionChanged += LookupSite_SelectionChanged;
            LookupSearchBox.QuerySubmitted += LookupSearchBox_QuerySubmitted;
            LookupSearchBox.TextChanged += LookupSearchBox_TextChanged;
        }

        private void LookupSite_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var site = (LookupSiteComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (!string.IsNullOrEmpty(site)) Services.SettingsService.LastLookupSite = site;
            var text = LookupSearchBox?.Text;
            if (!string.IsNullOrWhiteSpace(text) && ContentFrame.Content is Pages.WordLookupPage wp) wp.NavigateToSite(text);
        }

        private void LookupSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            var q = args.QueryText?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(q) && ContentFrame.Content is Pages.WordLookupPage wp) { wp.AddToHistory(q); wp.NavigateToSite(q); }
        }

        private void LookupSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var text = sender.Text ?? "";
            if (string.IsNullOrWhiteSpace(text)) { sender.ItemsSource = null; return; }
            var hist = Services.SettingsService.LookupHistory ?? new List<string>();
            var suggestions = hist.Where(h => h.StartsWith(text, StringComparison.OrdinalIgnoreCase)).Take(8).ToList();
            if (suggestions.Count > 0) sender.ItemsSource = suggestions;
        }

        private void TryLoadAppIcon()
        {
            try { var uri = Package.Current?.Logo; if (uri != null) { AppIconImage.Source = new BitmapImage(uri); return; } }
            catch { }
            AppIconImage.Source = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.png"));
        }

        private static T? ReadLocalSetting<T>(string key)
        {
            var values = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
            if (values.TryGetValue(key, out var obj) && obj is T t) return t;
            return default;
        }

        private static void WriteLocalSetting(string key, object value) => Windows.Storage.ApplicationData.Current.LocalSettings.Values[key] = value;
    }
}
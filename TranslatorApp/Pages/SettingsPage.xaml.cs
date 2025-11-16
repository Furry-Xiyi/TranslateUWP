using System;
using System.Threading.Tasks;
using TranslatorApp.Services;

// UWP 命名空间
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

// WinUI 2 命名空间别名（BackdropMaterial）
using muxc = Microsoft.UI.Xaml.Controls;

namespace TranslatorApp.Pages
{
    public sealed partial class SettingsPage : Page
    {
        private bool _isInitializing = true; // 初始化标志
        private string _lastBackdropTag;    // 记录上一次背景材质
                                            // 类字段
        private string _appliedThemeTag = "";   // "Default"/"Light"/"Dark"
        private string _appliedBackdropTag = ""; // "Mica"/"Acrylic"/"None" 
        public SettingsPage()
        {
            this.InitializeComponent();
            LoadSettings();
            _isInitializing = false; // 初始化完毕
        }

        private void LoadSettings()
        {
            // 主题
            var themeValue = ApplicationData.Current.LocalSettings.Values["AppTheme"] as string ?? "Default";
            RbTheme.SelectedIndex = themeValue switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0
            };

            // 背景材质
            var backdropValue = ApplicationData.Current.LocalSettings.Values["BackdropMaterial"] as string ?? "Mica";
            _lastBackdropTag = backdropValue;
            RbBackdrop.SelectedIndex = backdropValue switch
            {
                "None" => 0,
                "Mica" => 1,
                "Acrylic" => 2,
                _ => 1
            };

            // API Keys
            TbBingAppId.Text = SettingsService.BingAppId;
            TbBingSecret.Text = SettingsService.BingSecret;
            TbBaiduAppId.Text = SettingsService.BaiduAppId;
            TbBaiduSecret.Text = SettingsService.BaiduSecret;
            TbYoudaoAppKey.Text = SettingsService.YoudaoAppKey;
            TbYoudaoSecret.Text = SettingsService.YoudaoSecret;

            // 应用信息
            try
            {
                TxtAppName.Text = Package.Current.DisplayName;
                var v = Package.Current.Id.Version;
                TxtVersion.Text = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";

                var logoUri = Package.Current.Logo;
                ImgAppIcon.Source = new BitmapImage(logoUri);
            }
            catch
            {
                TxtAppName.Text = "未知";
                TxtVersion.Text = "版本号: 获取失败";
            }
        }

        private void RbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;

            var theme = ElementTheme.Default;
            if (RbTheme.SelectedItem is FrameworkElement item && item.Tag is string t)
            {
                theme = t switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };
            }

            var newThemeTag = (theme == ElementTheme.Default ? "Default" : (theme == ElementTheme.Light ? "Light" : "Dark"));

            // 幂等判断：如果值未变化，则不做任何事情，避免折叠面板等 UI 操作触发重复刷新
            if (string.Equals(newThemeTag, _appliedThemeTag, StringComparison.OrdinalIgnoreCase))
                return;

            _appliedThemeTag = newThemeTag;

            ApplicationData.Current.LocalSettings.Values["AppTheme"] = newThemeTag;

            if (Window.Current.Content is FrameworkElement fe)
                fe.RequestedTheme = theme;

            // 统一由 App 管理主题与 backdrop 的视觉表现
            var backdrop = ApplicationData.Current.LocalSettings.Values["BackdropMaterial"] as string ?? "Mica";
            ((App)Application.Current).ApplyThemeAndBackdrop(backdrop, theme);
        }

        private void RbBackdrop_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;

            var tag = (RbBackdrop.SelectedItem as RadioButton)?.Tag?.ToString() ?? "Mica";

            // 幂等判断：如果值未变化则直接返回，避免重复刷新
            if (string.Equals(tag, _appliedBackdropTag, StringComparison.OrdinalIgnoreCase))
                return;

            _appliedBackdropTag = tag;
            _lastBackdropTag = tag;

            ApplicationData.Current.LocalSettings.Values["BackdropMaterial"] = tag;

            var themeValue = ApplicationData.Current.LocalSettings.Values["AppTheme"] as string ?? "Default";
            var requested = themeValue switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };

            ((App)Application.Current).ApplyThemeAndBackdrop(tag, requested);
        }

        private string ReadBackdrop()
        {
            return ApplicationData.Current.LocalSettings.Values["BackdropMaterial"] as string ?? "Mica";
        }

        // ========== 输入为空校验（API Key 保存按钮） ==========
        private async void SaveBing_Click(object sender, RoutedEventArgs e)
        {
            var appId = TbBingAppId.Text?.Trim() ?? string.Empty;
            var secret = TbBingSecret.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(secret))
            {
                await new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "输入不能为空",
                    Content = new TextBlock
                    {
                        Text = "Bing 的 AppId 和 SecretKey 均不能为空，请填写后再保存。",
                        TextWrapping = TextWrapping.Wrap
                    },
                    CloseButtonText = "确定"
                }.ShowAsync();
                return;
            }

            await SaveApiKeyAsync("Bing", appId, secret);
        }

        private async void SaveBaidu_Click(object sender, RoutedEventArgs e)
        {
            var appId = TbBaiduAppId.Text?.Trim() ?? string.Empty;
            var secret = TbBaiduSecret.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(secret))
            {
                await new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "输入不能为空",
                    Content = new TextBlock
                    {
                        Text = "百度的 AppId 和 SecretKey 均不能为空，请填写后再保存。",
                        TextWrapping = TextWrapping.Wrap
                    },
                    CloseButtonText = "确定"
                }.ShowAsync();
                return;
            }

            await SaveApiKeyAsync("Baidu", appId, secret);
        }

        private async void SaveYoudao_Click(object sender, RoutedEventArgs e)
        {
            var appKey = TbYoudaoAppKey.Text?.Trim() ?? string.Empty;
            var secret = TbYoudaoSecret.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(appKey) || string.IsNullOrEmpty(secret))
            {
                await new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "输入不能为空",
                    Content = new TextBlock
                    {
                        Text = "有道的 AppKey 和 SecretKey 均不能为空，请填写后再保存。",
                        TextWrapping = TextWrapping.Wrap
                    },
                    CloseButtonText = "确定"
                }.ShowAsync();
                return;
            }

            await SaveApiKeyAsync("Youdao", appKey, secret);
        }

        private async Task SaveApiKeyAsync(string apiName, string key1, string key2 = "")
        {
            switch (apiName)
            {
                case "Bing":
                    SettingsService.BingAppId = key1;
                    SettingsService.BingSecret = key2;
                    SettingsService.BingApiKey = key2;
                    break;
                case "Baidu":
                    SettingsService.BaiduAppId = key1;
                    SettingsService.BaiduSecret = key2;
                    break;
                case "Youdao":
                    SettingsService.YoudaoAppKey = key1;
                    SettingsService.YoudaoSecret = key2;
                    break;
            }

            string verifyResult;
            try
            {
                verifyResult = await TranslationService.TranslateAsync(apiName, "Hello", "en", "zh");
            }
            catch
            {
                verifyResult = string.Empty;
            }

            if (string.IsNullOrWhiteSpace(verifyResult) ||
                verifyResult.Contains("失败") ||
                verifyResult.Contains("异常") ||
                verifyResult.Contains("无效"))
            {
                await new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = $"{apiName} API Key 验证失败",
                    Content = new TextBlock
                    {
                        Text = $"无法使用提供的 {apiName} API Key 进行翻译，请检查 Key 是否正确。",
                        TextWrapping = TextWrapping.Wrap
                    },
                    CloseButtonText = "确定"
                }.ShowAsync();
                return;
            }

            await new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "保存成功",
                Content = new TextBlock
                {
                    Text = $"{apiName} API Key 已验证并保存。",
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = "确定"
            }.ShowAsync();
        }
    }
}
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
            this.Loaded += (s, e) =>
            {
                LoadSettings();
                //InitializeDictionaryList();
            };
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
                _ => 2
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

        // 新增：点击“如何获取 API 密钥”的处理
        private async void HowToGetApi_Click(object sender, RoutedEventArgs e)
        {
            if (MainPage.Current != null)
            {
                await MainPage.Current.ShowApiHelpDialogAsync(false);
            }
        }

        public class DictionaryItem
        {
            public string Name { get; set; }
            public string Desc { get; set; }
            public string Status { get; set; }
            public string Type { get; set; }
        }

        /* private void InitializeDictionaryList()
        {
            try
            {
                if (this.FindName("DictionaryListPanel") is not StackPanel panel) return;

                var dicts = new[] {
                    ("雅思词典（IELTS）", "6000+ 雅思核心词汇", "IELTS", ""),
                    ("专业词典", "15000+ 专业领域术语", "Professional", ""),
                    ("GRE词典", "5000+ GRE高频词汇", "GRE", ""),
                    ("托福词典（TOEFL）", "4500+ 托福核心词汇", "TOEFL", "")
                };

                foreach (var (name, desc, type, downloadUrl) in dicts)
                {
                    // 整行 Border，横向拉伸
                    var border = new Border
                    {
                        Background = (Brush)Resources["CardBackgroundFillColorDefaultBrush"],
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(12),
                        Height = 90,
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    };

                    var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    // 左侧：词典名称、描述、状态
                    var stack = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                    stack.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = Windows.UI.Text.FontWeights.SemiBold });
                    stack.Children.Add(new TextBlock { Text = desc, FontSize = 12, Foreground = new SolidColorBrush(Colors.Gray) });
                    stack.Children.Add(new TextBlock { Text = "未安装", FontSize = 11, Opacity = 0.7, Tag = $"status_{type}" });

                    grid.Children.Add(stack);

                    // 右侧：下载按钮
                    var btn = new Button
                    {
                        Tag = type,
                        Padding = new Thickness(12, 8, 12, 8),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    btn.Click += DictButton_Click;
                    var btnStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                    btnStack.Children.Add(new SymbolIcon { Symbol = Symbol.Download });
                    btnStack.Children.Add(new TextBlock { Text = "下载", FontSize = 12 });
                    btn.Content = btnStack;
                    Grid.SetColumn(btn, 1);
                    grid.Children.Add(btn);

                    border.Child = grid;
                    panel.Children.Add(border);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"InitializeDictionaryList 异常: {ex}");
            }
        }

        private async void DictButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string typeStr)
            {
                var type = typeStr switch
                {
                    "IELTS" => DictionaryDownloadService.DictionaryType.IELTS,
                    "Professional" => DictionaryDownloadService.DictionaryType.Professional,
                    "GRE" => DictionaryDownloadService.DictionaryType.GRE,
                    "TOEFL" => DictionaryDownloadService.DictionaryType.TOEFL,
                    _ => DictionaryDownloadService.DictionaryType.IELTS
                };
                await DownloadDictionaryAsync(type, typeStr);
            }
        }

        private async Task DownloadDictionaryAsync(DictionaryDownloadService.DictionaryType type, string typeName)
        {
            try
            {
                MainPage.Current?.ShowInfo($"正在下载{typeName}词典，可能需要一些时间...");
                bool success = await DictionaryDownloadService.DownloadDictionaryAsync(type);

                if (success)
                {
                    MainPage.Current?.ShowSuccess($"{typeName}词典下载成功！");
                }
                else
                {
                    MainPage.Current?.ShowError($"{typeName}词典下载失败，请检查网络连接");
                }
            }
            catch (Exception ex)
            {
                MainPage.Current?.ShowError($"下载异常: {ex.Message}");
            }
        } */
    }
}

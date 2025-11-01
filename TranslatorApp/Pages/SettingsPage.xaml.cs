// Pages/SettingsPage.xaml.cs
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.ApplicationModel.Resources;
using muxc = Microsoft.UI.Xaml.Controls;

namespace TranslatorApp.Pages
{
    public sealed partial class SettingsPage : Page
    {
        private MainPage _main;
        private ResourceLoader _loader = ResourceLoader.GetForCurrentView();

        public SettingsPage()
        {
            InitializeComponent();
            Loaded += SettingsPage_Loaded;
        }

        private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;
            _main = frame?.Content as MainPage;
            LoadSaved();

            // load saved theme/material choices and apply
            var settings = ApplicationData.Current.LocalSettings;
            var theme = (settings.Values["App_Theme"] as string) ?? "Default";
            var material = (settings.Values["App_Material"] as string) ?? "Mica";

            // set radio buttons accordingly
            ThemeSystem.IsChecked = theme == "Default";
            ThemeLight.IsChecked = theme == "Light";
            ThemeDark.IsChecked = theme == "Dark";

            MaterialMica.IsChecked = material == "Mica";
            MaterialAcrylic.IsChecked = material == "Acrylic";
            MaterialMicaAlt.IsChecked = material == "None";

            // Apply saved settings to whole window
            ThemeHelper.ApplySavedThemeAndMaterial();
        }

        private void LoadSaved()
        {
            var s = ApplicationData.Current.LocalSettings;
            MS_Endpoint.Text = s.Values["MS_Endpoint"] as string ?? "";
            MS_Region.Text = s.Values["MS_Region"] as string ?? "";
            MS_Key.Password = s.Values["MS_Key"] as string ?? "";

            BD_AppId.Text = s.Values["BD_AppId"] as string ?? "";
            BD_Key.Password = s.Values["BD_Key"] as string ?? "";

            YD_AppKey.Text = s.Values["YD_AppKey"] as string ?? "";
            YD_Key.Password = s.Values["YD_Key"] as string ?? "";
        }

        private async Task<bool> ValidateMicrosoftAsync(string endpoint, string region, string key)
        {
            try
            {
                using var http = new HttpClient();
                var uri = $"{endpoint}/translate?api-version=3.0&from=en&to=zh-Hans";
                var req = new HttpRequestMessage(HttpMethod.Post, uri);
                req.Headers.Add("Ocp-Apim-Subscription-Key", key);
                if (!string.IsNullOrWhiteSpace(region))
                    req.Headers.Add("Ocp-Apim-Subscription-Region", region);
                req.Content = new StringContent("[{\"Text\":\"hello\"}]", Encoding.UTF8, "application/json");
                var resp = await http.SendAsync(req);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private async Task<bool> ValidateBaiduAsync(string appid, string key)
        {
            try
            {
                var salt = "12345";
                var sign = TranslatePage.Md5(appid + "test" + salt + key);
                var uri = $"https://fanyi-api.baidu.com/api/trans/vip/translate?q=test&from=en&to=zh&appid={appid}&salt={salt}&sign={sign}";
                using var http = new HttpClient();
                var resp = await http.GetAsync(uri);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private async Task<bool> ValidateYoudaoAsync(string appkey, string key)
        {
            try
            {
                var salt = Guid.NewGuid().ToString("N");
                var curtime = ((int)DateTimeOffset.Now.ToUnixTimeSeconds()).ToString();
                var input = TranslatePage.CompactInput("test");
                var signStr = appkey + input + salt + curtime + key;
                var sign = TranslatePage.Sha256(signStr);
                var uri = $"https://openapi.youdao.com/api?q=test&from=en&to=zh-CHS&appKey={appkey}&salt={salt}&sign={sign}&signType=v3&curtime={curtime}";
                using var http = new HttpClient();
                var resp = await http.GetAsync(uri);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private async void SaveAndValidate_MS_Click(object sender, RoutedEventArgs e)
        {
            await MaskAndValidate(async () =>
            {
                var ok = await ValidateMicrosoftAsync(MS_Endpoint.Text, MS_Region.Text, MS_Key.Password);
                if (ok)
                {
                    var s = ApplicationData.Current.LocalSettings;
                    s.Values["MS_Endpoint"] = MS_Endpoint.Text;
                    s.Values["MS_Region"] = MS_Region.Text;
                    s.Values["MS_Key"] = MS_Key.Password;
                    _main?.ShowSuccess(_loader.GetString("ValidationSavedSuccess"));
                }
                else _main?.ShowError(_loader.GetString("ValidationFailed"));
            });
        }

        private async void SaveAndValidate_BD_Click(object sender, RoutedEventArgs e)
        {
            await MaskAndValidate(async () =>
            {
                var ok = await ValidateBaiduAsync(BD_AppId.Text, BD_Key.Password);
                if (ok)
                {
                    var s = ApplicationData.Current.LocalSettings;
                    s.Values["BD_AppId"] = BD_AppId.Text;
                    s.Values["BD_Key"] = BD_Key.Password;
                    _main?.ShowSuccess(_loader.GetString("ValidationSavedSuccess"));
                }
                else _main?.ShowError(_loader.GetString("ValidationFailed"));
            });
        }

        private async void SaveAndValidate_YD_Click(object sender, RoutedEventArgs e)
        {
            await MaskAndValidate(async () =>
            {
                var ok = await ValidateYoudaoAsync(YD_AppKey.Text, YD_Key.Password);
                if (ok)
                {
                    var s = ApplicationData.Current.LocalSettings;
                    s.Values["YD_AppKey"] = YD_AppKey.Text;
                    s.Values["YD_Key"] = YD_Key.Password;
                    _main?.ShowSuccess(_loader.GetString("ValidationSavedSuccess"));
                }
                else _main?.ShowError(_loader.GetString("ValidationFailed"));
            });
        }

        // 遮罩屏幕 + Ring（这里简单用 InfoBar + 延时模拟）
        private async Task MaskAndValidate(Func<Task> validator)
        {
            _main?.ShowInfo(_loader.GetString("MaskValidating"));
            await Task.Delay(800);
            await validator();
        }

        private void Theme_Checked(object sender, RoutedEventArgs e)
        {
            string themeStr = "Default";
            if (ThemeSystem.IsChecked == true)
                RequestedTheme = ElementTheme.Default;
            else if (ThemeLight.IsChecked == true)
            {
                RequestedTheme = ElementTheme.Light;
                themeStr = "Light";
            }
            else if (ThemeDark.IsChecked == true)
            {
                RequestedTheme = ElementTheme.Dark;
                themeStr = "Dark";
            }

            // persist
            ApplicationData.Current.LocalSettings.Values["App_Theme"] = themeStr;

            // Apply to root
            var rootControl = Window.Current.Content as Control;
            if (rootControl != null)
            {
                rootControl.RequestedTheme = this.RequestedTheme;
            }

            //主题切换时刷新材质（保证 Acrylic/None 跟随主题色）
            ThemeHelper.ApplySavedThemeAndMaterial();
        }

        private void Material_Checked(object sender, RoutedEventArgs e)
        {
            string mat = "Mica";
            if (MaterialMica.IsChecked == true) mat = "Mica";
            else if (MaterialAcrylic.IsChecked == true) mat = "Acrylic";
            else if (MaterialMicaAlt.IsChecked == true) mat = "None";

            ApplicationData.Current.LocalSettings.Values["App_Material"] = mat;

            ThemeHelper.ApplySavedThemeAndMaterial();
        }

        private void ApplyMaterial()
        {
            // legacy kept for compatibility — now centralized in ThemeHelper
            ThemeHelper.ApplySavedThemeAndMaterial();
        }

        private void SendFeedback_Click(object sender, RoutedEventArgs e)
        {
            _main?.ShowInfo(_loader.GetString("ThanksFeedback"));
        }
    }
}
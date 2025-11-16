// Pages/TranslatePage.xaml.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Media.Ocr;
using Windows.Media.SpeechRecognition;
using Windows.Media.SpeechSynthesis;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace TranslatorApp.Pages
{
    public sealed partial class TranslatePage : Page
    {
        private MainPage _main;
        private SpeechRecognizer _speechRecognizer;
        private SpeechSynthesizer _speechSynth;

        public TranslatePage()
        {
            InitializeComponent();
            Loaded += TranslatePage_Loaded;
        }

        private void TranslatePage_Loaded(object sender, RoutedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;
            _main = frame?.Content as MainPage;

            // 初始化语言代码（示例，生产中请使用完整映射）
            InputLangCombo.ItemsSource = new[] { "zh-Hans", "en", "ja", "ko", "fr", "de", "es", "ru" };
            OutputLangCombo.ItemsSource = new[] { "en", "zh-Hans", "ja", "ko", "fr", "de", "es", "ru" };
            InputLangCombo.SelectedIndex =0;
            OutputLangCombo.SelectedIndex =1;

            ProviderCombo.ItemsSource = new[] { "Microsoft/Bing", "Baidu", "Youdao" };
            ProviderCombo.SelectedIndex =0;

            _speechSynth = new SpeechSynthesizer();
        }

        private void SwapLanguages_Click(object sender, RoutedEventArgs e)
        {
            var inIdx = InputLangCombo.SelectedIndex;
            InputLangCombo.SelectedIndex = OutputLangCombo.SelectedIndex;
            OutputLangCombo.SelectedIndex = inIdx;
        }

        private async void SpeechInput_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 麦克风权限需在清单启用（microphone），典型流程参考社区与文档
                _speechRecognizer ??= new SpeechRecognizer();
                await _speechRecognizer.CompileConstraintsAsync();
                var result = await _speechRecognizer.RecognizeAsync();
                if (result.Status == SpeechRecognitionResultStatus.Success)
                {
                    InputText.Text = result.Text;
                    _main?.ShowSuccess("语音输入成功");
                }
                else
                {
                    _main?.ShowWarning("语音识别失败");
                }
            }
            catch
            {
                _main?.ShowError("语音输入异常");
            }
        }

        private async void OcrInput_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".bmp");
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;

                using var stream = await file.OpenReadAsync();
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                var softwareBmp = await decoder.GetSoftwareBitmapAsync();
                var ocr = OcrEngine.TryCreateFromLanguage(new Language("zh-Hans"));
                var res = await ocr.RecognizeAsync(softwareBmp);
                InputText.Text = res.Text;
                _main?.ShowSuccess("OCR识别成功");
            }
            catch
            {
                _main?.ShowError("OCR识别失败");
            }
        }

        private void CopyInput_Click(object sender, RoutedEventArgs e)
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(InputText.Text ?? "");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            _main?.ShowSuccess("已复制输入文本");
        }

        private async void SpeakOutput_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var stream = await _speechSynth.SynthesizeTextToStreamAsync(OutputText.Text ?? "");
                var media = new MediaElement();
                media.SetSource(stream, stream.ContentType);
                media.Play();
                _main?.ShowSuccess("朗读开始");
            }
            catch
            {
                _main?.ShowError("朗读失败");
            }
        }

        private void CopyOutput_Click(object sender, RoutedEventArgs e)
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(OutputText.Text ?? "");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            _main?.ShowSuccess("已复制输出文本");
        }

        private void FavoriteOutput_Click(object sender, RoutedEventArgs e)
        {
            var settings = ApplicationData.Current.LocalSettings;
            var favorites = (settings.Values["favorites_text"] as string) ?? "";
            var set = new HashSet<string>(favorites.Split('|').Where(s => !string.IsNullOrWhiteSpace(s)));
            var key = $"text::{InputText.Text}::{OutputText.Text}::provider={ProviderCombo.SelectedItem}::ts={DateTimeOffset.Now.ToUnixTimeMilliseconds()}";
            set.Add(key);
            settings.Values["favorites_text"] = string.Join("|", set);
            _main?.ShowSuccess("已收藏翻译结果");
        }

        private async void TranslateButton_Click(object sender, RoutedEventArgs e)
        {
            var provider = ProviderCombo.SelectedItem?.ToString();
            var text = InputText.Text?.Trim() ?? "";
            var from = InputLangCombo.SelectedItem?.ToString() ?? "auto";
            var to = OutputLangCombo.SelectedItem?.ToString() ?? "zh-Hans";
            if (string.IsNullOrEmpty(text)) return;

            // 设置页已保存的凭据状态
            var settings = ApplicationData.Current.LocalSettings;

            try
            {
                string translated = "";
                if (provider == "Microsoft/Bing")
                {
                    // Microsoft Translator Text API（Azure Cognitive Services）示例
                    var endpoint = settings.Values["MS_Endpoint"] as string ?? "https://api.cognitive.microsofttranslator.com";
                    var region = settings.Values["MS_Region"] as string ?? "";
                    var key = settings.Values["MS_Key"] as string ?? "";

                    using var http = new HttpClient();
                    var uri = $"{endpoint}/translate?api-version=3.0&from={from}&to={to}";
                    var req = new HttpRequestMessage(HttpMethod.Post, uri);
                    req.Headers.Add("Ocp-Apim-Subscription-Key", key);
                    if (!string.IsNullOrWhiteSpace(region))
                        req.Headers.Add("Ocp-Apim-Subscription-Region", region);
                    // fixed malformed interpolation
                    req.Content = new StringContent($"[{{\"Text\":\"{EscapeJson(text)}\"}}]", Encoding.UTF8, "application/json");

                    var resp = await http.SendAsync(req);
                    var json = await resp.Content.ReadAsStringAsync();
                    translated = ExtractMicrosoftResult(json);
                }
                else if (provider == "Baidu")
                {
                    // 百度翻译（通用API）：sign=md5(appid+q+salt+key)
                    var appid = settings.Values["BD_AppId"] as string ?? "";
                    var key = settings.Values["BD_Key"] as string ?? "";
                    var salt = new Random().Next(100000,999999).ToString();
                    var sign = Md5(appid + text + salt + key);
                    var uri = $"https://fanyi-api.baidu.com/api/trans/vip/translate?q={Uri.EscapeDataString(text)}&from={from}&to={to}&appid={appid}&salt={salt}&sign={sign}";

                    using var http = new HttpClient();
                    var json = await http.GetStringAsync(uri);
                    translated = ExtractBaiduResult(json);
                }
                else if (provider == "Youdao")
                {
                    // 有道翻译：sign=sha256(appKey+input+salt+curtime+key) 等（老/新接口略有差异）
                    var appKey = settings.Values["YD_AppKey"] as string ?? "";
                    var key = settings.Values["YD_Key"] as string ?? "";
                    var salt = Guid.NewGuid().ToString("N");
                    var curtime = ((int)DateTimeOffset.Now.ToUnixTimeSeconds()).ToString();
                    var input = CompactInput(text);
                    var signStr = appKey + input + salt + curtime + key;
                    var sign = Sha256(signStr);
                    var uri = $"https://openapi.youdao.com/api?q={Uri.EscapeDataString(text)}&from={from}&to={to}&appKey={appKey}&salt={salt}&sign={sign}&signType=v3&curtime={curtime}";

                    using var http = new HttpClient();
                    var json = await http.GetStringAsync(uri);
                    translated = ExtractYoudaoResult(json);
                }

                OutputText.Text = translated;
                _main?.ShowSuccess("翻译完成");
            }
            catch
            {
                _main?.ShowError("翻译失败，请检查配置与网络");
            }
        }

        public static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        public static string Md5(string s)
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
            return string.Concat(hash.Select(b => b.ToString("x2")));
        }
        public static string Sha256(string s)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
            return string.Concat(hash.Select(b => b.ToString("x2")));
        }
        public static string CompactInput(string q) => q.Length <=20 ? q : q.Substring(0,10) + q.Length + q.Substring(q.Length -10);

        private static string ExtractMicrosoftResult(string json)
        {
            // 简化解析：实际请用 JsonObject/Json.NET
            var idx = json.IndexOf("\"text\":");
            if (idx <0) return "";
            var start = json.IndexOf('"', idx +7);
            var end = json.IndexOf('"', start +1);
            return json.Substring(start +1, end - start -1);
        }
        private static string ExtractBaiduResult(string json)
        {
            var idx = json.IndexOf("\"dst\":");
            if (idx <0) return "";
            var start = json.IndexOf('"', idx +6);
            var end = json.IndexOf('"', start +1);
            return json.Substring(start +1, end - start -1);
        }
        private static string ExtractYoudaoResult(string json)
        {
            var idx = json.IndexOf("\"tgt\":");
            if (idx <0)
            {
                idx = json.IndexOf("\"translation\":[");
                if (idx >=0)
                {
                    var start = json.IndexOf('"', idx +15);
                    var end = json.IndexOf('"', start +1);
                    return json.Substring(start +1, end - start -1);
                }
                return "";
            }
            var s = json.IndexOf('"', idx +6);
            var e = json.IndexOf('"', s +1);
            return json.Substring(s +1, e - s -1);
        }
    }
}
// Win2D
using Microsoft.Graphics.Canvas;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TranslatorApp.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Enumeration;
// WinRT interop for GraphicsCapturePicker (UWP)
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Media.SpeechRecognition;
using Windows.Media.SpeechSynthesis;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;

namespace TranslatorApp.Pages
{
    public sealed partial class OnlineTranslatePage : Page
    {
        private SpeechSynthesizer _synth;
        private MediaElement _player;

        private CancellationTokenSource _cts;

        // 语音识别状态
        private SpeechRecognizer _continuousRecognizer;
        private Task _recognizerStopTask;
        private bool _isListening = false;
        private bool _isMicBusy = false;

        // 冷却时间
        private DateTime _lastStopTime = DateTime.MinValue;
        private readonly TimeSpan _micRestartCooldown = TimeSpan.FromMilliseconds(500);

        private bool _isStarting = false;

        public OnlineTranslatePage()
        {
            InitializeComponent();
            Loaded += OnlineTranslatePage_Loaded;
            Unloaded += OnlineTranslatePage_Unloaded;
        }

        private void OnlineTranslatePage_Loaded(object sender, RoutedEventArgs e)
        {
            _synth = new SpeechSynthesizer();
            _player = new MediaElement { AutoPlay = true };

            LoadLanguageOptions();
            LoadApiOptions();
            BtnTranslate.IsEnabled = !string.IsNullOrWhiteSpace(TbInput.Text);
        }

        private async void OnlineTranslatePage_Unloaded(object sender, RoutedEventArgs e)
        {
            try { _synth?.Dispose(); } catch { }
            _synth = null;

            try { _cts?.Cancel(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _cts = null;

            var recognizer = _continuousRecognizer;
            _continuousRecognizer = null;
            if (recognizer != null)
            {
                try
                {
                    recognizer.ContinuousRecognitionSession.ResultGenerated -= OnResultGenerated;
                    try { await recognizer.ContinuousRecognitionSession.StopAsync(); } catch { }
                }
                catch { }
                finally
                {
                    try { recognizer.Dispose(); } catch { }
                }
            }
        }
        // MD5 返回小写十六进制字符串
        public static string Md5(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            using var md5 = System.Security.Cryptography.MD5.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(input);
            var hash = md5.ComputeHash(bytes);
            var sb = new System.Text.StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // SHA256 返回小写十六进制字符串
        public static string Sha256(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(input);
            var hash = sha.ComputeHash(bytes);
            var sb = new System.Text.StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // 有道 v3 compactInput 规则：长度 <= 20 返回原文，否则 前10 + length + 后10
        public static string CompactInput(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            if (input.Length <= 20) return input;
            return string.Concat(input.Substring(0, 10), input.Length.ToString(), input.Substring(input.Length - 10));
        }
        private void LoadLanguageOptions()
        {
            string[] langs = { "自动检测", "中文", "英文", "日文", "韩文" };
            foreach (var lang in langs)
            {
                CbFromLang.Items.Add(new ComboBoxItem { Content = lang, Tag = GetLangTag(lang) });
                CbToLang.Items.Add(new ComboBoxItem { Content = lang, Tag = GetLangTag(lang) });
            }
            CbFromLang.SelectedIndex = 0;
            CbToLang.SelectedIndex = 1;
        }

        private string GetLangTag(string lang) => lang switch
        {
            "自动检测" => "auto",
            "中文" => "zh",
            "英文" => "en",
            "日文" => "ja",
            "韩文" => "ko",
            _ => "auto"
        };

        private void LoadApiOptions()
        {
            CbApi.Items.Clear();
            CbApi.Items.Add(new ComboBoxItem { Content = "Bing", Tag = "Bing" });
            CbApi.Items.Add(new ComboBoxItem { Content = "百度", Tag = "Baidu" });
            CbApi.Items.Add(new ComboBoxItem { Content = "有道", Tag = "Youdao" });

            var lastApi = SettingsService.LastUsedApi;
            foreach (ComboBoxItem item in CbApi.Items)
            {
                if ((item.Tag as string) == lastApi)
                {
                    CbApi.SelectedItem = item;
                    break;
                }
            }
            if (CbApi.SelectedItem == null) CbApi.SelectedIndex = 0;

            CbApi.SelectionChanged += (s, e) =>
            {
                if (CbApi.SelectedItem is ComboBoxItem selected)
                    SettingsService.LastUsedApi = selected.Tag as string ?? "Bing";
            };
        }

        private void BtnSwapLang_Click(object sender, RoutedEventArgs e)
        {
            var inIndex = CbFromLang.SelectedIndex;
            var outIndex = CbToLang.SelectedIndex;
            CbFromLang.SelectedIndex = outIndex;
            CbToLang.SelectedIndex = inIndex;

            var rotate = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = new Duration(TimeSpan.FromMilliseconds(400)),
                EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(rotate, SwapRotate);
            Storyboard.SetTargetProperty(rotate, "Angle");
            var sb = new Storyboard();
            sb.Children.Add(rotate);
            sb.Begin();
        }

        private void TbInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            BtnTranslate.IsEnabled = !string.IsNullOrWhiteSpace(TbInput.Text);
            if (string.IsNullOrWhiteSpace(TbInput.Text))
            {
                TbOutput.Text = string.Empty;
            }
        }

        private async void BtnCopyInput_Click(object sender, RoutedEventArgs e)
        {
            var dp = new DataPackage();
            dp.SetText(TbInput.Text ?? string.Empty);
            Clipboard.SetContent(dp);
            AppToast("已复制输入文本");
            await Task.Delay(300);
        }

        private async void BtnCopyOutput_Click(object sender, RoutedEventArgs e)
        {
            var dp = new DataPackage();
            dp.SetText(TbOutput.Text ?? string.Empty);
            Clipboard.SetContent(dp);
            AppToast("已复制翻译结果");
            await Task.Delay(300);
        }

        private async void BtnSpeakOutput_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TbOutput.Text) || _synth == null || _player == null) return;
            try
            {
                var stream = await _synth.SynthesizeTextToStreamAsync(TbOutput.Text);
                _player.SetSource(stream, stream.ContentType);
                _player.Play();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        private void BtnFavOutput_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(TbOutput.Text))
            {
                FavoritesService.Add(TbOutput.Text.Trim());
                AppToast("已添加到收藏");
            }
        }

        private async void BtnTranslate_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TbInput.Text))
                return;

            var api = (CbApi.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Bing";

            bool hasKey = api switch
            {
                "Bing" => !string.IsNullOrWhiteSpace(SettingsService.BingApiKey),
                "Baidu" => !string.IsNullOrWhiteSpace(SettingsService.BaiduAppId) &&
                           !string.IsNullOrWhiteSpace(SettingsService.BaiduSecret),
                "Youdao" => !string.IsNullOrWhiteSpace(SettingsService.YoudaoAppKey) &&
                            !string.IsNullOrWhiteSpace(SettingsService.YoudaoSecret),
                _ => false
            };

            if (!hasKey)
            {
                var dlg = new ContentDialog
                {
                    Title = $"{api} API 未填写",
                    Content = new TextBlock { Text = "请前往设置页填写 API 密钥。" },
                    PrimaryButtonText = "去设置",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary
                };
                var r = await dlg.ShowAsync();
                if (r == ContentDialogResult.Primary)
                {
                    await Task.Delay(200);
                    Frame.Navigate(typeof(SettingsPage));
                }
                return;
            }

            var sourceLang = ((CbFromLang.SelectedItem as ComboBoxItem)?.Tag?.ToString()) ?? "auto";
            var targetLang = ((CbToLang.SelectedItem as ComboBoxItem)?.Tag?.ToString()) ?? "zh";
            var text = TbInput.Text?.Trim() ?? string.Empty;

            if (!string.IsNullOrEmpty(text) && sourceLang != "auto" && sourceLang == targetLang)
            {
                TbOutput.Text = text;
                return;
            }

            SetBusy(true, api);

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            try
            {
                var result = await TranslationService.TranslateAsync(
                    provider: api,
                    text: text,
                    from: sourceLang,
                    to: targetLang,
                    cancellationToken: _cts.Token);

                TbOutput.Text = result;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await new ContentDialog
                {
                    Title = "翻译失败",
                    Content = new ScrollViewer
                    {
                        Content = new TextBlock
                        {
                            Text = ex.Message,
                            TextWrapping = TextWrapping.Wrap
                        }
                    },
                    CloseButtonText = "确定"
                }.ShowAsync();
            }
            finally
            {
                SetBusy(false, api);
                _cts?.Dispose();
                _cts = null;
            }
        }

        // ====== 语音识别 ======
        private async void BtnMicInput_Click(object sender, RoutedEventArgs e)
        {
            if (_isMicBusy || _isStarting) return;

            if (!_isListening && (DateTime.Now - _lastStopTime) < _micRestartCooldown)
            {
                BtnMicInput.IsEnabled = false;
                await Task.Delay(_micRestartCooldown);
                BtnMicInput.IsEnabled = true;
                return;
            }

            try
            {
                _isMicBusy = true;

                if (!_isListening)
                {
                    _isStarting = true;

                    BtnMicInput.IsHitTestVisible = false;
                    _ = Task.Delay(1000).ContinueWith(_ =>
                    {
                        var ignore = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                        {
                            BtnMicInput.IsHitTestVisible = true;
                        });
                    });

                    await AwaitPreviousStopAsync(TimeSpan.FromSeconds(1));

                    var devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
                    if (devices == null || devices.Count == 0)
                    {
                        await ShowSettingDialogAsync(
                            "需要麦克风权限",
                            "请在系统设置中开启麦克风权限以使用语音识别功能。",
                            "ms-settings:privacy-microphone"
                        );
                        SetMicListeningState(false);
                        return;
                    }

                    _continuousRecognizer = new SpeechRecognizer();
                    await _continuousRecognizer.CompileConstraintsAsync();
                    _continuousRecognizer.ContinuousRecognitionSession.ResultGenerated += OnResultGenerated;

                    try
                    {
                        await _continuousRecognizer.ContinuousRecognitionSession.StartAsync();
                    }
                    catch (COMException comEx) when ((uint)comEx.HResult == 0x80045509 || SpeechRecognizer.SystemSpeechLanguage == null)
                    {
                        await ShowSettingDialogAsync(
                            "联机语音识别未启用",
                            "请先在 Windows 设置 → 隐私和安全 → 语音 中开启“联机语音识别”并接受隐私政策。",
                            "ms-settings:privacy-speech"
                        );
                        TryDisposeRecognizer();
                        SetMicListeningState(false);
                        return;
                    }
                    catch (COMException comEx) when ((uint)comEx.HResult == 0x800455BC)
                    {
                        AppToast("设备正忙，请稍后再试");
                        TryDisposeRecognizer();
                        SetMicListeningState(false);
                        return;
                    }
                    finally
                    {
                        _isStarting = false;
                    }

                    AppToast("请说...");
                    SetMicListeningState(true);
                }
                else
                {
                    SetMicListeningState(false);
                    AppToast("已停止录音");
                    _lastStopTime = DateTime.Now;

                    BtnMicInput.IsEnabled = false;

                    var recognizer = _continuousRecognizer;
                    _continuousRecognizer = null;
                    if (recognizer != null)
                    {
                        try { recognizer.ContinuousRecognitionSession.ResultGenerated -= OnResultGenerated; } catch { }

                        _recognizerStopTask = Task.Run(async () =>
                        {
                            try
                            {
                                try { await recognizer.ContinuousRecognitionSession.StopAsync(); } catch { }
                                try
                                {
                                    var finalTask = recognizer.RecognizeAsync().AsTask();
                                    var done = await Task.WhenAny(finalTask, Task.Delay(600));
                                    if (done == finalTask)
                                    {
                                        var finalResult = await finalTask;
                                        if (finalResult.Status == SpeechRecognitionResultStatus.Success &&
                                            !string.IsNullOrWhiteSpace(finalResult.Text))
                                        {
                                            var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                                            {
                                                if (string.IsNullOrWhiteSpace(TbInput.Text))
                                                    TbInput.Text = finalResult.Text;
                                                else
                                                    TbInput.Text += " " + finalResult.Text;
                                            });
                                        }
                                    }
                                }
                                catch { }
                            }
                            finally
                            {
                                TryDisposeRecognizer(recognizer);
                            }
                        });
                    }

                    await Task.Delay(1500);
                    BtnMicInput.IsEnabled = true;
                }
            }
            finally
            {
                _isMicBusy = false;
                _isStarting = false;
            }
        }

        private void OnResultGenerated(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionResultGeneratedEventArgs args)
        {
            var recognized = args.Result.Text;
            if (!string.IsNullOrWhiteSpace(recognized))
            {
                var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (string.IsNullOrWhiteSpace(TbInput.Text))
                        TbInput.Text = recognized;
                    else
                        TbInput.Text += " " + recognized;
                });
            }
        }

        private async Task AwaitPreviousStopAsync(TimeSpan timeout)
        {
            var t = _recognizerStopTask;
            if (t == null) return;
            try { await Task.WhenAny(t, Task.Delay(timeout)); } catch { }
        }

        private void SetMicListeningState(bool listening)
        {
            if (listening)
            {
                // 使用系统强调样式近似 AccentButtonStyle
                BtnMicInput.Background = new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SystemAccentColor"]);
                BtnMicInput.Foreground = new SolidColorBrush(Windows.UI.Colors.White);
            }
            else
            {
                BtnMicInput.ClearValue(Button.BackgroundProperty);
                BtnMicInput.ClearValue(Button.ForegroundProperty);
            }
            _isListening = listening;
        }

        private async Task ShowSettingDialogAsync(string title, string message, string settingUri)
        {
            var dlg = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "打开设置",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                await Launcher.LaunchUriAsync(new Uri(settingUri));
            }
        }

        private void ResetMicButtonState()
        {
            BtnMicInput.ClearValue(Button.StyleProperty);
            _isListening = false;
            BtnMicInput.IsEnabled = true;
        }

        private void SetBusy(bool isBusy, string apiLabel)
        {
            BtnTranslate.IsEnabled = !isBusy && !string.IsNullOrWhiteSpace(TbInput.Text);
            CbApi.IsEnabled = !isBusy;
            CbFromLang.IsEnabled = !isBusy;
            CbToLang.IsEnabled = !isBusy;
            TbInput.IsEnabled = !isBusy;

            BtnTranslate.Content = isBusy ? $"翻译中…（{apiLabel}）" : "翻译";
        }

        // ====== Win2D 截图 + OCR ======
        private async Task<SoftwareBitmap?> PickImageAndDecodeAsync()
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");

            StorageFile file = await picker.PickSingleFileAsync();
            if (file == null) return null;

            try
            {
                using (IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read))
                {
                    var decoder = await BitmapDecoder.CreateAsync(stream);
                    var sb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    return sb;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"图片解码失败: {ex}");
                return null;
            }
        }

        private async Task<string> RunOcrAsync(SoftwareBitmap? bitmap)
        {
            if (bitmap == null) return string.Empty;

            var engine = OcrEngine.TryCreateFromLanguage(new Language("zh-CN"))
                         ?? OcrEngine.TryCreateFromUserProfileLanguages();

            if (engine == null) return string.Empty;

            try
            {
                var result = await engine.RecognizeAsync(bitmap);
                return result?.Text ?? string.Empty;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OCR 失败: {ex}");
                return string.Empty;
            }
        }
        private async void BtnOcrCapture_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var bitmap = await PickImageAndDecodeAsync();
                if (bitmap == null)
                {
                    AppToast("未选择图片");
                    return;
                }

                var text = await RunOcrAsync(bitmap);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    TbInput.Text = text;
                    BtnTranslate.IsEnabled = true;
                    AppToast("OCR识别完成");
                }
                else
                {
                    AppToast("未识别到文字");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OCR异常: {ex}");
                AppToast("OCR识别出错");
            }
        }
        private void AppToast(string message)
        {
            // 将提示交由主页面（UWP简易InfoBar）或在此用 Toast/TextBlock
            // 这里简单使用 ContentDialog 模拟轻提示，或替换为你的 MainPage.ShowToast
            // 如果你有 MainPage.Current.ShowToast，可改用那种方式：
            var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                var tip = new ContentDialog
                {
                    Title = "提示",
                    Content = new TextBlock { Text = message },
                    CloseButtonText = "确定"
                };
                await tip.ShowAsync();
            });
        }

        private void TryDisposeRecognizer(SpeechRecognizer r = null)
        {
            try { (r ?? _continuousRecognizer)?.Dispose(); } catch { }
        }
    }
}
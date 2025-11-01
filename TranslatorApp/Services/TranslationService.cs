using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace TranslatorApp.Services
{
    public static class TranslationService
    {
        private static readonly HttpClient _http = new();

        public static async Task<string> TranslateAsync(string provider, string text, string from, string to, CancellationToken cancellationToken = default)
        {
            return provider switch
            {
                "Bing" => await TranslateWithBingAsync(text, from, to, cancellationToken),
                "Baidu" => await TranslateWithBaiduAsync(text, from, to, cancellationToken),
                "Youdao" => await TranslateWithYoudaoAsync(text, from, to, cancellationToken),
                _ => "不支持的翻译 API"
            };
        }

        // Bing 翻译（Azure Cognitive Services）
        private static async Task<string> TranslateWithBingAsync(string text, string from, string to, CancellationToken ct)
        {
            var key = SettingsService.BingApiKey;
            if (string.IsNullOrWhiteSpace(key)) return "Bing API Key 未填写";

            var endpoint = "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0";
            var url = $"{endpoint}&from={from}&to={to}";

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Ocp-Apim-Subscription-Key", key);
            request.Headers.Add("Ocp-Apim-Subscription-Region", "global"); // 可改为你的区域

            var body = new[] { new { Text = text } };
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            try
            {
                var response = await _http.SendAsync(request, ct);
                var json = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode) return $"Bing翻译失败：{response.StatusCode}";

                using var doc = JsonDocument.Parse(json);
                var translations = doc.RootElement[0].GetProperty("translations");
                var result = translations[0].GetProperty("text").GetString();
                return result ?? "翻译结果为空";
            }
            catch (Exception ex)
            {
                return $"Bing翻译异常：{ex.Message}";
            }
        }

        // 百度翻译（通用版 API）
        private static async Task<string> TranslateWithBaiduAsync(string text, string from, string to, CancellationToken ct)
        {
            var appid = SettingsService.BaiduAppId;
            var secret = SettingsService.BaiduSecret;
            if (string.IsNullOrWhiteSpace(appid) || string.IsNullOrWhiteSpace(secret))
                return "百度 API Key 未填写";

            var salt = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();
            var sign = ComputeMD5(appid + text + salt + secret);
            var url = $"https://fanyi-api.baidu.com/api/trans/vip/translate?q={Uri.EscapeDataString(text)}&from={from}&to={to}&appid={appid}&salt={salt}&sign={sign}";

            try
            {
                var json = await _http.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("error_code", out var err))
                {
                    return $"百度翻译失败：{err.GetString()}";
                }

                var transResult = doc.RootElement.GetProperty("trans_result");
                var result = transResult[0].GetProperty("dst").GetString();
                return result ?? "翻译结果为空";
            }
            catch (Exception ex)
            {
                return $"百度翻译异常：{ex.Message}";
            }
        }

        private static string ComputeMD5(string input)
        {
            using var md5 = MD5.Create();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder();
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // 有道翻译（通用版 API）
        private static async Task<string> TranslateWithYoudaoAsync(string text, string from, string to, CancellationToken ct)
        {
            var appKey = SettingsService.YoudaoAppKey;
            var secret = SettingsService.YoudaoSecret;
            if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(secret))
                return "有道 API Key 未填写";

            var salt = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();
            var curtime = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();

            var signStr = appKey + Truncate(text) + salt + curtime + secret;
            var sign = ComputeSHA256(signStr);

            var url = "https://openapi.youdao.com/api";
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("q", text),
                new KeyValuePair<string, string>("from", from),
                new KeyValuePair<string, string>("to", to),
                new KeyValuePair<string, string>("appKey", appKey),
                new KeyValuePair<string, string>("salt", salt),
                new KeyValuePair<string, string>("sign", sign),
                new KeyValuePair<string, string>("signType", "v3"),
                new KeyValuePair<string, string>("curtime", curtime)
            });

            try
            {
                var response = await _http.PostAsync(url, content, ct);
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("errorCode", out var err) && err.GetString() != "0")
                {
                    return $"有道翻译失败：{err.GetString()}";
                }

                var translation = doc.RootElement.GetProperty("translation");
                var result = translation[0].GetString();
                return result ?? "翻译结果为空";
            }
            catch (Exception ex)
            {
                return $"有道翻译异常：{ex.Message}";
            }
        }

        private static string Truncate(string q)
        {
            if (q.Length <= 20) return q;
            return q.Substring(0, 10) + q.Length + q.Substring(q.Length - 10);
        }

        private static string ComputeSHA256(string input)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder();
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
using Windows.Storage;
using Windows.Data.Text; // 核心命名空间

namespace TranslatorApp.Services
{
    public static class LocalTranslationService
    {
        private static Dictionary<string, string> _dict;

        private static async Task EnsureLoadedAsync()
        {
            if (_dict != null) return;
            try
            {
                var file = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/dict/dict.json"));
                var json = await FileIO.ReadTextAsync(file);
                _dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Local] 词库加载失败: {ex.Message}");
                _dict = new Dictionary<string, string>();
            }
        }

        public static async Task<string> TranslateLocalAsync(string text, string languageTag)
        {
            await EnsureLoadedAsync();
            if (string.IsNullOrWhiteSpace(text)) return text;

            // 1. 尝试“全句匹配” (解决类似 Morning Good 的语序问题)
            // 只要 JSON 里有 "good morning": "早上好"，这里就会直接返回正确结果
            string inputLower = text.Trim().ToLower();
            if (_dict.TryGetValue(inputLower, out string fullMatch))
            {
                return fullMatch;
            }

            // 2. 逐词匹配 (解决空格丢失问题)
            string tag = (string.IsNullOrEmpty(languageTag) || languageTag == "auto") ? "en-US" : languageTag;

            try
            {
                // 使用 SelectableWordsSegmenter 替代 WordsSegmenter
                // 它的特点是：会保留单词后面的空格和标点
                var segmenter = new SelectableWordsSegmenter(tag);
                var tokens = segmenter.GetTokens(text);

                var result = "";
                foreach (var token in tokens)
                {
                    // token.Text 包含了原始文本块（比如 "Hello " 包含空格）
                    string originalBlock = token.Text;

                    // 我们只拿“词”的部分去查字典，去掉空格和标点后再查
                    string cleanWord = originalBlock.Trim().ToLower();

                    // 移除末尾标点（简单处理，确保 apple. 也能匹配 apple）
                    cleanWord = cleanWord.TrimEnd('.', ',', '!', '?', ';');

                    if (_dict.TryGetValue(cleanWord, out string translated))
                    {
                        // 如果查到了，替换词的部分，但保留原来的空格（比如 "你好 "）
                        // 这里的处理比较粗犷，如果是中翻英，可能需要手动补空格
                        result += translated + (originalBlock.EndsWith(" ") ? " " : "");
                    }
                    else
                    {
                        // 没查到，原样保留（包括原来的空格和标点）
                        result += originalBlock;
                    }
                }
                return result.Trim();
            }
            catch
            {
                return text;
            }
        }
    }
}

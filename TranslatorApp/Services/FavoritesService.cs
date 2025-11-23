#nullable enable
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using TranslatorApp.Models;
using Windows.Storage;
using System;
using System.Threading.Tasks;
using System.IO;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using System.Net;

namespace TranslatorApp.Services
{
    public static class FavoritesService
    {
        private const string Key = "FavoritesJson";
        private const string CloudFileName = "favorites.json"; // 存放在 App Root
        private static readonly ApplicationDataContainer Local = ApplicationData.Current.LocalSettings;

        // 缓存集合，保证永远不为 null
        private static ObservableCollection<FavoriteItem> _cache = new();

        public static ObservableCollection<FavoriteItem> Items
        {
            get => _cache;
            set => _cache = value ?? new ObservableCollection<FavoriteItem>();
        }

        /// <summary>
        /// 从本地存储加载收藏数据到缓存，并尝试从云端合并
        /// </summary>
        public static async void Load()
        {
            LoadLocal();
            await LoadFromCloudAsync();
        }

        private static void LoadLocal()
        {
            if (Local.Values.ContainsKey(Key))
            {
                var json = Local.Values[Key] as string;
                if (!string.IsNullOrWhiteSpace(json))
                {
                    try
                    {
                        var list = JsonSerializer.Deserialize<ObservableCollection<FavoriteItem>>(json);
                        _cache = list ?? new ObservableCollection<FavoriteItem>();
                    }
                    catch
                    {
                        _cache = new ObservableCollection<FavoriteItem>();
                    }
                }
                else
                {
                    _cache = new ObservableCollection<FavoriteItem>();
                }
            }
            else
            {
                _cache = new ObservableCollection<FavoriteItem>();
            }
        }

        public static void Add(string term)
        {
            if (string.IsNullOrWhiteSpace(term)) return;

            if (!_cache.Any(i => i.Term.Equals(term, StringComparison.OrdinalIgnoreCase)))
            {
                _cache.Add(new FavoriteItem { Term = term });
                Save();
            }
        }

        public static void Remove(FavoriteItem item)
        {
            if (_cache.Remove(item))
                Save();
        }

        public static void Save()
        {
            var json = JsonSerializer.Serialize(_cache);
            Local.Values[Key] = json;

            // 异步同步到云端
            _ = SyncToCloudAsync();
        }

        /// <summary>
        /// 将本地收藏同步到 OneDrive App Root: /me/drive/special/approot:/favorites.json:/content
        /// </summary>
        private static async Task SyncToCloudAsync()
        {
            try
            {
                if (App.GraphClient == null) return; // 未登录微软账号
                var adapter = App.GraphClient.RequestAdapter;

                var json = JsonSerializer.Serialize(_cache);
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

                var requestInfo = new RequestInformation
                {
                    HttpMethod = Method.PUT,
                    UrlTemplate = "{+baseurl}/me/drive/special/approot:/{itemPath}:/content"
                };
                requestInfo.PathParameters.Add("itemPath", CloudFileName);
                requestInfo.SetStreamContent(stream, "application/json");

                // 返回 DriveItem，但这里不需要使用结果
                await adapter.SendAsync<DriveItem>(requestInfo, DriveItem.CreateFromDiscriminatorValue);
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FavoritesService] 云同步失败: {ex.ResponseStatusCode} {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FavoritesService] 云同步失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从 OneDrive App Root 加载收藏并合并到本地
        /// </summary>
        private static async Task LoadFromCloudAsync()
        {
            try
            {
                if (App.GraphClient == null) return;
                var adapter = App.GraphClient.RequestAdapter;

                var requestInfo = new RequestInformation
                {
                    HttpMethod = Method.GET,
                    UrlTemplate = "{+baseurl}/me/drive/special/approot:/{itemPath}:/content"
                };
                requestInfo.PathParameters.Add("itemPath", CloudFileName);

                using var stream = await adapter.SendPrimitiveAsync<Stream>(requestInfo);
                if (stream == null) return;

                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync();

                if (!string.IsNullOrWhiteSpace(json))
                {
                    var cloudList = JsonSerializer.Deserialize<ObservableCollection<FavoriteItem>>(json)
                                    ?? new ObservableCollection<FavoriteItem>();

                    // 合并云端数据到本地（避免重复）
                    foreach (var item in cloudList)
                    {
                        if (!_cache.Any(i => i.Term.Equals(item.Term, StringComparison.OrdinalIgnoreCase)))
                        {
                            _cache.Add(item);
                        }
                    }

                    // 保存合并后的数据到本地（同时触发云端更新）
                    Save();
                }
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == (int)HttpStatusCode.NotFound)
            {
                // 云端文件不存在，忽略
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FavoritesService] 云加载失败: {ex.ResponseStatusCode} {ex.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FavoritesService] 云加载失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 导出收藏为 JSON 文件到用户选择的位置
        /// </summary>
        public static async Task<bool> ExportFavoritesAsync()
        {
            try
            {
                var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
                var fileName = $"favorites_{DateTime.Now:yyyy-MM-dd_HHmmss}.json";

                // 使用 FileSavePicker 让用户选择保存位置
                var savePicker = new Windows.Storage.Pickers.FileSavePicker();
                savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                savePicker.FileTypeChoices.Add("JSON 文件", new System.Collections.Generic.List<string> { ".json" });
                savePicker.SuggestedFileName = fileName;

                var file = await savePicker.PickSaveFileAsync();
                if (file != null)
                {
                    await FileIO.WriteTextAsync(file, json);
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 从 JSON 文件导入收藏
        /// </summary>
        public static async Task<bool> ImportFavoritesAsync()
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add(".json");
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;

                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    var json = await FileIO.ReadTextAsync(file);
                    var imported = JsonSerializer.Deserialize<ObservableCollection<FavoriteItem>>(json);

                    if (imported != null)
                    {
                        // 合并导入数据（避免重复）
                        foreach (var item in imported)
                        {
                            if (!_cache.Any(i => i.Term.Equals(item.Term, StringComparison.OrdinalIgnoreCase)))
                            {
                                _cache.Add(item);
                            }
                        }
                        Save();
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }
    }
}
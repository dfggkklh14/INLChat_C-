using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace Client.Utility
{
    public static class CacheHelper
    {
        private static readonly ILogger _logger = NullLogger.Instance;
        private static string _cachePath = null;
        private static bool _cachePathLoaded = false;

        /// <summary>
        /// 获取缓存路径（优先 config.json，否则默认 Chat_DATA）
        /// </summary>
        private static string GetCachePath()
        {
            if (_cachePathLoaded)
                return _cachePath;

            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    var config = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                    if (config != null && config.TryGetValue("cache_path", out var configuredPath) && !string.IsNullOrWhiteSpace(configuredPath))
                    {
                        _cachePath = configuredPath;
                        _logger.LogDebug($"从 config.json 读取 cache_path: {_cachePath}");
                    }
                }
                if (string.IsNullOrEmpty(_cachePath))
                {
                    _cachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Chat_DATA");
                    _logger.LogDebug($"未定义 cache_path，使用默认路径: {_cachePath}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"读取缓存路径失败: {ex.Message}");
                _cachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Chat_DATA");
            }
            finally
            {
                _cachePathLoaded = true;
            }
            return _cachePath;
        }

        /// <summary>
        /// 获取头像文件的路径，如果文件存在则返回完整路径，否则返回 null。
        /// 优先从 config.json 的 cache_path 获取缓存路径，默认路径为 Chat_DATA/Avatars。
        /// </summary>
        /// <param name="avatarId">头像 ID（文件名）</param>
        /// <returns>头像文件的完整路径，或 null（如果文件不存在）</returns>
        public static string GetAvatarPath(string avatarId)
        {
            if (string.IsNullOrEmpty(avatarId))
            {
                _logger.LogWarning("AvatarId 为空，无法获取头像路径");
                return null;
            }

            try
            {
                string cachePath = GetCachePath();
                string avatarsPath = Path.Combine(cachePath, "Avatars");
                string avatarPath = Path.Combine(avatarsPath, avatarId);

                if (File.Exists(avatarPath))
                {
                    _logger.LogDebug($"找到头像文件: {avatarPath}");
                    return avatarPath;
                }

                _logger.LogDebug($"头像文件不存在: {avatarPath}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError($"获取头像路径失败: avatarId={avatarId}, 错误: {ex.Message}");
                return null;
            }
        }
    }
}
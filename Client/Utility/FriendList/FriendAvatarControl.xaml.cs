using Client.Function;
using Client.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Client.Utility.FriendList
{
    public partial class FriendAvatarControl : UserControl
    {
        private readonly ILogger<FriendAvatarControl> _logger;
        private bool _isCreatedByFriendListControl; // 标志位，判断是否由 FriendListControl 创建

        public static readonly DependencyProperty AvatarIdProperty =
            DependencyProperty.Register(
                "AvatarId",
                typeof(string),
                typeof(FriendAvatarControl),
                new PropertyMetadata(null, OnAvatarIdChanged));

        public static readonly DependencyProperty OnlineProperty =
            DependencyProperty.Register(
                "Online",
                typeof(bool),
                typeof(FriendAvatarControl),
                new PropertyMetadata(false, OnOnlineChanged));

        public static readonly DependencyProperty LinkProperty =
            DependencyProperty.Register(
                "Link",
                typeof(Link),
                typeof(FriendAvatarControl),
                new PropertyMetadata(null));

        public static readonly DependencyProperty IsCreatedByFriendListControlProperty =
            DependencyProperty.Register(
                "IsCreatedByFriendListControl",
                typeof(bool),
                typeof(FriendAvatarControl),
                new PropertyMetadata(false, OnIsCreatedByFriendListControlChanged));

        public string AvatarId
        {
            get => (string)GetValue(AvatarIdProperty);
            set => SetValue(AvatarIdProperty, value);
        }

        public bool Online
        {
            get => (bool)GetValue(OnlineProperty);
            set => SetValue(OnlineProperty, value);
        }

        public Link Link
        {
            get => (Link)GetValue(LinkProperty);
            set => SetValue(LinkProperty, value);
        }

        public bool IsCreatedByFriendListControl
        {
            get => (bool)GetValue(IsCreatedByFriendListControlProperty);
            set => SetValue(IsCreatedByFriendListControlProperty, value);
        }

        public FriendAvatarControl()
        {
            InitializeComponent();
            var app = Application.Current as App;
            _logger = app?.LogFactory?.CreateLogger<FriendAvatarControl>() ?? NullLogger<FriendAvatarControl>.Instance;
            _logger.LogDebug("FriendAvatarControl 初始化");

            // 在构造时立即设置默认头像
            SetDefaultAvatar();
        }

        private static void OnIsCreatedByFriendListControlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (FriendAvatarControl)d;
            control._isCreatedByFriendListControl = (bool)e.NewValue;
            _ = control.LoadAvatarAsync(control.AvatarId); // 重新触发加载逻辑
        }

        private static async void OnAvatarIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (FriendAvatarControl)d;
            var newAvatarId = e.NewValue as string;
            await control.LoadAvatarAsync(newAvatarId);
        }

        private static void OnOnlineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (FriendAvatarControl)d;
            var isOnline = (bool)e.NewValue;
            control.UpdateOnlineStatus(isOnline);
        }

        private async Task LoadAvatarAsync(string avatarId)
        {
            try
            {
                if (string.IsNullOrEmpty(avatarId))
                {
                    _logger.LogDebug("AvatarId 为空，设置默认头像");
                    SetDefaultAvatar();
                    return;
                }

                // 检查本地缓存
                string avatarPath = CacheHelper.GetAvatarPath(avatarId);
                if (avatarPath != null)
                {
                    _logger.LogDebug($"找到缓存头像: {avatarPath}");
                    SetAvatarFromFile(avatarPath);
                    return;
                }

                // 如果由 FriendListControl 创建，依赖其批量下载
                if (_isCreatedByFriendListControl)
                {
                    _logger.LogDebug($"头像未缓存，等待 FriendListControl 批量下载: {avatarId}");
                    SetDefaultAvatar();
                }
                else
                {
                    // 否则执行单独下载
                    _logger.LogDebug($"头像未缓存，开始单独下载: {avatarId}");
                    await DownloadAvatarAsync(avatarId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"加载头像失败: {ex.Message}");
                SetDefaultAvatar();
            }
        }

        private async Task DownloadAvatarAsync(string avatarId)
        {
            try
            {
                if (Link == null)
                {
                    _logger.LogWarning("Link 未设置，无法下载头像");
                    SetDefaultAvatar();
                    return;
                }

                // 获取缓存路径
                string cachePath = CacheHelper.GetAvatarPath("dummy") != null
                    ? Path.GetDirectoryName(CacheHelper.GetAvatarPath("dummy"))
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Chat_DATA", "Avatars");
                Directory.CreateDirectory(cachePath);

                string savePath = Path.Combine(cachePath, avatarId);

                // 调用 Link.DownloadMedia 下载头像
                var fileRequests = new List<(string fileId, string savePath)> { (avatarId, savePath) };
                var results = await Link.DownloadMedia(fileRequests, "avatar");

                var result = results.FirstOrDefault(r => r.GetValueOrDefault("file_id")?.ToString() == avatarId);
                if (result != null && result.GetValueOrDefault("status")?.ToString() == "success" && File.Exists(savePath))
                {
                    _logger.LogDebug($"头像下载成功: {avatarId}, 保存路径: {savePath}");
                    SetAvatarFromFile(savePath);
                }
                else
                {
                    _logger.LogWarning($"头像下载失败: {avatarId}, 原因: {result?.GetValueOrDefault("message")}");
                    SetDefaultAvatar();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"下载头像失败: {avatarId}, 错误: {ex.Message}");
                SetDefaultAvatar();
            }
        }

        private void SetDefaultAvatar()
        {
            string defaultAvatarPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon", "default_avatar.png");
            if (File.Exists(defaultAvatarPath))
            {
                _logger.LogDebug($"加载默认头像: {defaultAvatarPath}");
                Dispatcher.Invoke(() => AvatarBrush.ImageSource = LoadBitmapImage(defaultAvatarPath));
            }
            else
            {
                _logger.LogWarning($"默认头像文件不存在: {defaultAvatarPath}");
                Dispatcher.Invoke(() => AvatarBrush.ImageSource = null);
            }
        }

        public void SetAvatarFromFile(string filePath)
        {
            try
            {
                var image = LoadBitmapImage(filePath);
                if (image != null)
                {
                    if (Dispatcher.CheckAccess())
                    {
                        AvatarBrush.ImageSource = image;
                    }
                    else
                    {
                        Dispatcher.Invoke(() => AvatarBrush.ImageSource = image);
                    }
                }
                else
                {
                    _logger.LogWarning($"从文件加载头像失败，使用默认头像: {filePath}");
                    SetDefaultAvatar();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"从文件加载头像失败: {filePath}, 错误: {ex.Message}");
                SetDefaultAvatar();
            }
        }

        private BitmapImage LoadBitmapImage(string filePath)
        {
            try
            {
                // 验证文件是否存在且不为空
                if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
                {
                    _logger.LogWarning($"图像文件不存在或为空: {filePath}");
                    return null;
                }

                // 检查 JPEG 文件头，排除 default_avatar.png
                if (Path.GetFileName(filePath) != "default_avatar.png")
                {
                    using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                    {
                        byte[] header = new byte[2];
                        int bytesRead = stream.Read(header, 0, 2);
                        if (bytesRead != 2 || header[0] != 0xFF || header[1] != 0xD8)
                        {
                            _logger.LogWarning($"图像文件格式无效（非 JPEG）: {filePath}");
                            return null;
                        }
                    }
                }

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                _logger.LogError($"加载图像失败: {filePath}, 错误: {ex.Message}");
                return null;
            }
        }

        private void UpdateOnlineStatus(bool isOnline)
        {
            try
            {
                var onlineDotAnimation = (Storyboard)Resources["OnlineDotAnimation"];
                var offlineDotAnimation = (Storyboard)Resources["OfflineDotAnimation"];
                var onlineBorderAnimation = (Storyboard)Resources["OnlineBorderAnimation"];
                var offlineBorderAnimation = (Storyboard)Resources["OfflineBorderAnimation"];

                if (isOnline)
                {
                    _logger.LogDebug("播放在线状态动画");
                    onlineDotAnimation.Begin();
                    onlineBorderAnimation.Begin();
                }
                else
                {
                    _logger.LogDebug("播放离线状态动画");
                    offlineDotAnimation.Begin();
                    offlineBorderAnimation.Begin();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"更新在线状态动画失败: {ex.Message}");
            }
        }
    }

    public class AvatarIdToImageSourceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
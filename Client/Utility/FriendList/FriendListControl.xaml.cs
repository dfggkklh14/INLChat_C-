using Client.Function;
using Client.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Client.Utility.FriendList
{
    public partial class FriendListControl : UserControl
    {
        private readonly ILogger<FriendListControl> _logger;

        public static readonly DependencyProperty FriendsProperty =
        DependencyProperty.Register(
        "Friends",
        typeof(ObservableCollection<FriendModel>),
        typeof(FriendListControl),
        new PropertyMetadata(null));

        public static readonly DependencyProperty LinkProperty =
        DependencyProperty.Register(
        "Link",
        typeof(Link),
        typeof(FriendListControl),
        new PropertyMetadata(null, OnLinkChanged));

        public ObservableCollection<FriendModel> Friends
        {
            get => (ObservableCollection<FriendModel>)GetValue(FriendsProperty);
            set => SetValue(FriendsProperty, value);
        }

        public Link Link
        {
            get => (Link)GetValue(LinkProperty);
            set => SetValue(LinkProperty, value);
        }

        public FriendListControl()
        {
            InitializeComponent();
            Friends = new ObservableCollection<FriendModel>();
            BindingOperations.EnableCollectionSynchronization(Friends, new object());
            FriendListBox.ItemContainerGenerator.ItemsChanged += ItemContainerGenerator_ItemsChanged;

            var app = Application.Current as App;
            _logger = app?.LogFactory?.CreateLogger<FriendListControl>() ?? NullLogger<FriendListControl>.Instance;
            _logger.LogDebug("FriendListControl 初始化");
        }

        private void ItemContainerGenerator_ItemsChanged(object sender, System.Windows.Controls.Primitives.ItemsChangedEventArgs e)
        {
            UpdateFriendAvatarLinks();
        }

        private static void OnLinkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (FriendListControl)d;
            var oldLink = e.OldValue as Link;
            var newLink = e.NewValue as Link;

            if (oldLink != null)
            {
                oldLink.FriendListUpdated -= control.Link_FriendListUpdated;
            }

            if (newLink != null)
            {
                control.UpdateFriends(newLink.Friends);
                newLink.FriendListUpdated += control.Link_FriendListUpdated;
            }
        }

        private void Link_FriendListUpdated(List<Dictionary<string, object>> friends)
        {
            Dispatcher.Invoke(() =>
            {
                UpdateFriends(friends);
            });
        }

        private void UpdateFriends(List<Dictionary<string, object>> friends)
        {
            var friendModels = friends.Select(f => FriendModel.FromDictionary(f, Link?.UnreadMessages ?? new Dictionary<string, int>())).ToList();
            Friends.Clear();
            foreach (var friend in friendModels)
            {
                Friends.Add(friend);
            }

            // 收集所有需要下载的 AvatarId
            var avatarIdsToDownload = friendModels
            .Where(f => !string.IsNullOrEmpty(f.AvatarId) && CacheHelper.GetAvatarPath(f.AvatarId) == null)
            .Select(f => f.AvatarId)
            .Distinct()
            .ToList();

            if (avatarIdsToDownload.Any())
            {
                _logger.LogDebug($"需要下载的头像: {string.Join(", ", avatarIdsToDownload)}");
                Task.Run(() => DownloadAvatarsAsync(avatarIdsToDownload));
            }

            Dispatcher.InvokeAsync(UpdateFriendAvatarLinks, System.Windows.Threading.DispatcherPriority.Background);
        }

        private async Task DownloadAvatarsAsync(List<string> avatarIds)
        {
            try
            {
                _logger.LogDebug($"开始批量下载头像: {string.Join(", ", avatarIds)}, 线程 ID: {Thread.CurrentThread.ManagedThreadId}");

                // 使用 CacheHelper 获取缓存路径
                string cachePath = CacheHelper.GetAvatarPath("dummy") != null
                ? Path.GetDirectoryName(CacheHelper.GetAvatarPath("dummy"))
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Chat_DATA", "Avatars");
                Directory.CreateDirectory(cachePath);

                var fileRequests = avatarIds.Select(id => (fileId: id, savePath: Path.Combine(cachePath, id))).ToList();

                // 使用 ExecuteOnDispatcher 调用 Link.DownloadMedia
                List<Dictionary<string, object>> results = await ExecuteOnDispatcher(async () =>
                {
                    _logger.LogDebug($"Link.DownloadMedia 调用，UI 线程 ID: {Thread.CurrentThread.ManagedThreadId}");
                    return await Link.DownloadMedia(fileRequests, "avatar");
                }).ConfigureAwait(false);

                foreach (var result in results)
                {
                    var fileId = result.GetValueOrDefault("file_id")?.ToString();
                    var savePath = result.GetValueOrDefault("save_path")?.ToString();
                    if (result.GetValueOrDefault("status")?.ToString() == "success" && File.Exists(savePath))
                    {
                        _logger.LogDebug($"头像下载成功: {fileId}, 保存路径: {savePath}");
                        await ExecuteOnDispatcher(() =>
                        {
                            _logger.LogDebug($"UI 更新，线程 ID: {Thread.CurrentThread.ManagedThreadId}");
                            var friend = Friends.FirstOrDefault(f => f.AvatarId == fileId);
                            if (friend != null)
                            {
                                var listBoxItem = FriendListBox.ItemContainerGenerator.ContainerFromItem(friend) as ListBoxItem;
                                if (listBoxItem != null)
                                {
                                    var friendAvatar = FindVisualChild<FriendAvatarControl>(listBoxItem);
                                    if (friendAvatar != null)
                                    {
                                        friendAvatar.SetAvatarFromFile(savePath);
                                        _logger.LogDebug($"更新头像成功: {fileId}");
                                    }
                                    else
                                    {
                                        _logger.LogWarning($"未找到 FriendAvatarControl: {fileId}");
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning($"未找到 ListBoxItem: {fileId}");
                                }
                            }
                            else
                            {
                                _logger.LogWarning($"未找到匹配的好友: {fileId}");
                            }
                        }).ConfigureAwait(false);
                    }
                    else
                    {
                        _logger.LogWarning($"头像下载失败: {fileId}, 原因: {result.GetValueOrDefault("message")}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"批量下载头像失败: {ex.Message}, 堆栈: {ex.StackTrace}");
            }
        }

        private async Task<T> ExecuteOnDispatcher<T>(Func<Task<T>> action)
        {
            _logger.LogDebug($"ExecuteOnDispatcher 开始，调用线程 ID: {Thread.CurrentThread.ManagedThreadId}");
            Task<T> innerTask = await Dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
            _logger.LogDebug($"ExecuteOnDispatcher 完成，UI 线程 ID: {Thread.CurrentThread.ManagedThreadId}");
            return await innerTask.ConfigureAwait(false);
        }

        private async Task ExecuteOnDispatcher(Action action)
        {
            _logger.LogDebug($"ExecuteOnDispatcher (Action) 开始，调用线程 ID: {Thread.CurrentThread.ManagedThreadId}");
            await Dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
            _logger.LogDebug($"ExecuteOnDispatcher (Action) 完成，UI 线程 ID: {Thread.CurrentThread.ManagedThreadId}");
        }

        private void UpdateFriendAvatarLinks()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(UpdateFriendAvatarLinks);
                return;
            }

            foreach (var item in FriendListBox.Items)
            {
                var listBoxItem = FriendListBox.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
                if (listBoxItem != null)
                {
                    var friendAvatar = FindVisualChild<FriendAvatarControl>(listBoxItem);
                    if (friendAvatar != null && Link != null)
                    {
                        friendAvatar.Link = Link;
                    }
                }
            }
        }

        private T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T target)
                {
                    return target;
                }
                var result = FindVisualChild<T>(child);
                if (result != null)
                {
                    return result;
                }
            }
            return null;
        }
    }

    public class ZeroToVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is int count && count == 0)
                return Visibility.Visible;
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class NonZeroToVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is int count && count > 0)
                return Visibility.Visible;
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class NullToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value == null ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class UnreadAndConversationToVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (values.Length == 2 && values[0] is int unreadCount && values[1] is object conversation)
            {
                if (unreadCount == 0 && conversation != null)
                    return Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
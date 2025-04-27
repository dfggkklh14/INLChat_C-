using Client.Function;
using Client.page;
using Client.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
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
using System.Windows.Input;
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

            // 增量更新 Friends 集合
            var existingUsernames = Friends.Select(f => f.Username).ToHashSet();
            var newUsernames = friendModels.Select(f => f.Username).ToHashSet();

            // 移除不存在的好友
            for (int i = Friends.Count - 1; i >= 0; i--)
            {
                if (!newUsernames.Contains(Friends[i].Username))
                {
                    Friends.RemoveAt(i);
                }
            }

            // 添加或更新好友
            foreach (var newFriend in friendModels)
            {
                var existingFriend = Friends.FirstOrDefault(f => f.Username == newFriend.Username);
                if (existingFriend == null)
                {
                    Friends.Add(newFriend);
                }
                else
                {
                    // 更新现有好友的属性
                    existingFriend.AvatarId = newFriend.AvatarId;
                    existingFriend.Name = newFriend.Name;
                    existingFriend.Sign = newFriend.Sign;
                    existingFriend.Online = newFriend.Online;
                    existingFriend.Conversation = newFriend.Conversation;
                    existingFriend.UnreadCount = newFriend.UnreadCount;
                }
            }

            // 收集需要下载的 AvatarId
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
                        friendAvatar.IsCreatedByFriendListControl = true; // 设置标志
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
        private async void FriendListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _logger.LogDebug("FriendListBox_SelectionChanged 触发");
            var chatWindow = Window.GetWindow(this) as ChatWindow;
            _logger.LogDebug($"chatWindow 类型: {chatWindow?.GetType()?.FullName}, ChatArea 存在: {chatWindow?.ChatArea != null}, IconImg 存在: {chatWindow?.IconImg != null}");
            if (FriendListBox.SelectedItem is FriendModel selectedFriend)
            {
                string friendUsername = selectedFriend.Username;
                _logger.LogDebug($"用户选择了好友: {friendUsername}");

                if (chatWindow?.ChatArea != null && chatWindow?.IconImg != null)
                {
                    chatWindow.ChatArea.Visibility = Visibility.Visible;
                    chatWindow.IconImg.Visibility = Visibility.Collapsed;
                    chatWindow.ChatArea.UpdateFriendInfo(selectedFriend);
                    _logger.LogDebug($"ChatArea已设置为可见，IconImg已隐藏，好友: {friendUsername}, ChatArea Visibility: {chatWindow.ChatArea.Visibility}, IconImg Visibility: {chatWindow.IconImg.Visibility}");
                }
                else
                {
                    _logger.LogError("无法找到ChatWindow、ChatArea或IconImg");
                }

                await GetChatHistory(friendUsername);
            }
            else
            {
                _logger.LogDebug("无好友选中");
                if (chatWindow?.ChatArea != null && chatWindow?.IconImg != null)
                {
                    chatWindow.ChatArea.Visibility = Visibility.Collapsed;
                    chatWindow.IconImg.Visibility = Visibility.Visible;
                    chatWindow.ChatArea.ClearFriendInfo();
                    _logger.LogDebug($"ChatArea已隐藏，IconImg已显示，ChatArea Visibility: {chatWindow.ChatArea.Visibility}, IconImg Visibility: {chatWindow.IconImg.Visibility}");
                }
                else
                {
                    _logger.LogError("无法找到ChatWindow、ChatArea或IconImg（取消选择时）");
                }
            }
        }

        // 获取聊天记录的方法
        private async Task GetChatHistory(string friendUsername)
        {
            if (Link == null)
            {
                _logger.LogError("Link 实例未初始化，无法获取聊天记录");
                return;
            }

            try
            {
                Link.CurrentFriend = friendUsername;
                int page = 1;
                int pageSize = 10;
                _logger.LogDebug($"开始获取 {friendUsername} 的聊天记录，page={page}, pageSize={pageSize}");
                var response = await Link.GetChatHistoryPaginated(friendUsername, page, pageSize);
                _logger.LogDebug($"聊天记录响应: {JsonConvert.SerializeObject(response)}");

                if (response.ContainsKey("errors"))
                {
                    _logger.LogError($"聊天记录解析包含错误: {JsonConvert.SerializeObject(response["errors"])}");
                    throw new InvalidOperationException("聊天记录解析失败");
                }

                var chatHistory = response.GetValueOrDefault("data") as List<Dictionary<string, object>>;
                if (chatHistory == null)
                {
                    _logger.LogError("聊天记录数据格式错误: data 不是 List<Dictionary<string, object>>");
                    throw new InvalidOperationException("聊天记录数据格式错误");
                }

                _logger.LogInformation($"成功获取 {friendUsername} 的聊天记录，共 {chatHistory.Count} 条");
                // 可选择将 chatHistory 存储或传递给 UI
            }
            catch (Exception ex)
            {
                _logger.LogError($"获取 {friendUsername} 的聊天记录失败: {ex.Message}, StackTrace: {ex.StackTrace}");
                throw; // 暂时保留 throw，以便调试
            }
        }

        private void FriendListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                _logger.LogDebug("FriendListBox 检测到 Esc 键，取消选择好友");
                FriendListBox.SelectedItem = null; // 触发 SelectionChanged
                e.Handled = true; // 防止事件冒泡
            }
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
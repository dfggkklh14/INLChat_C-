using Client.Function;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;

namespace Client.Utility
{
    public partial class FriendListControl : UserControl
    {
        private readonly Link _link;
        private readonly ILogger<FriendListControl> _logger;
        private readonly ObservableCollection<FriendItemViewModel> _friends;
        private readonly string _avatarDir;
        private readonly SemaphoreSlim _updateLock;

        public event EventHandler<string> FriendSelected;

        public FriendListControl()
        {
            InitializeComponent();
            _friends = new ObservableCollection<FriendItemViewModel>();
            FriendListBox.ItemsSource = _friends;
            _updateLock = new SemaphoreSlim(1, 1);

            // 初始化缓存目录
            string cacheRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Chat_DATA");
            _avatarDir = Path.Combine(cacheRoot, "avatars");
            Directory.CreateDirectory(_avatarDir);
        }

        public FriendListControl(Link link, ILoggerFactory loggerFactory) : this()
        {
            _link = link;
            _logger = loggerFactory.CreateLogger<FriendListControl>();
            _link.FriendListUpdated += OnFriendListUpdated;
            _link.ConversationsUpdated += OnConversationsUpdated;
            _logger.LogDebug("FriendListControl 构造函数完成");
        }

        private async void OnFriendListUpdated(List<Dictionary<string, object>> friends)
        {
            _logger.LogDebug("收到 OnFriendListUpdated 事件，朋友列表长度: {0}", friends?.Count ?? 0);
            await UpdateFriendList(friends);
        }

        private async void OnConversationsUpdated(List<Dictionary<string, object>> friends, List<string> affectedUsers, List<int> deletedRowids, bool showFloatingLabel)
        {
            _logger.LogDebug("收到 OnConversationsUpdated 事件，朋友列表长度: {0}, 受影响用户: {1}", friends?.Count ?? 0, affectedUsers?.Count ?? 0);
            if (affectedUsers?.Any() == true)
            {
                await UpdateFriendList(affectedUsers: affectedUsers);
            }
            else
            {
                await UpdateFriendList(friends);
            }
        }

        private List<Dictionary<string, object>> SortFriends(List<Dictionary<string, object>> friends)
        {
            // 过滤无效好友
            var validFriends = friends.Where(f => f != null && f.ContainsKey("username") && f["username"] != null).ToList();

            // 分离在线和离线好友
            var online = validFriends
                .Where(f => Convert.ToBoolean(f.GetValueOrDefault("online", false)))
                .OrderByDescending(f =>
                {
                    var conv = f.GetValueOrDefault("conversations") as Dictionary<string, object>;
                    return conv?.GetValueOrDefault("last_update_time")?.ToString() ?? "1970-01-01 00:00:00";
                })
                .ToList();

            var offline = validFriends
                .Where(f => !Convert.ToBoolean(f.GetValueOrDefault("online", false)))
                .OrderByDescending(f =>
                {
                    var conv = f.GetValueOrDefault("conversations") as Dictionary<string, object>;
                    return conv?.GetValueOrDefault("last_update_time")?.ToString() ?? "1970-01-01 00:00:00";
                })
                .ToList();

            return online.Concat(offline).ToList();
        }

        public async Task UpdateFriendList(List<Dictionary<string, object>> friends = null, List<string> affectedUsers = null)
        {
            await _updateLock.WaitAsync();
            try
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    _logger.LogInformation("开始更新好友列表，friends={0}, affectedUsers={1}",
                        friends?.Count ?? 0, affectedUsers?.Count ?? 0);

                    // 获取好友列表
                    friends = friends ?? _link.Friends ?? new List<Dictionary<string, object>>();
                    var uniqueFriends = friends
                        .Where(f => f != null && f.ContainsKey("username") && f["username"] != null)
                        .GroupBy(f => f["username"].ToString())
                        .Select(g => g.First())
                        .ToList();

                    var sortedFriends = SortFriends(uniqueFriends);
                    string currentFriend = _link.CurrentFriend;

                    if (affectedUsers?.Any() == true)
                    {
                        // 精准更新
                        foreach (var uname in affectedUsers)
                        {
                            var friend = sortedFriends.FirstOrDefault(f => f.GetValueOrDefault("username")?.ToString() == uname);
                            if (friend == null)
                            {
                                _logger.LogWarning("精准更新未找到好友: {0}", uname);
                                continue;
                            }

                            var viewModel = _friends.FirstOrDefault(vm => vm.Username == uname);
                            bool isNew = viewModel == null;
                            if (isNew)
                            {
                                viewModel = new FriendItemViewModel();
                                _friends.Add(viewModel);
                            }

                            UpdateViewModel(viewModel, friend);
                            await UpdateAvatarAsync(viewModel);

                            // 确保列表顺序正确
                            int currentIndex = _friends.IndexOf(viewModel);
                            int correctIndex = sortedFriends.FindIndex(f => f.GetValueOrDefault("username")?.ToString() == uname);
                            if (currentIndex != correctIndex && correctIndex >= 0)
                            {
                                _friends.Move(currentIndex, correctIndex);
                            }
                        }
                    }
                    else
                    {
                        // 全量更新
                        _logger.LogDebug("执行全量更新，清除现有好友列表");
                        _friends.Clear();
                        foreach (var friend in sortedFriends)
                        {
                            var viewModel = new FriendItemViewModel();
                            UpdateViewModel(viewModel, friend);
                            _friends.Add(viewModel);
                            await UpdateAvatarAsync(viewModel);
                        }
                    }

                    // 恢复当前选择
                    if (!string.IsNullOrEmpty(currentFriend))
                    {
                        var selectedFriend = _friends.FirstOrDefault(vm => vm.Username == currentFriend);
                        if (selectedFriend != null)
                        {
                            FriendListBox.SelectedItem = selectedFriend;
                            _logger.LogDebug("恢复选择好友: {0}", currentFriend);
                        }
                    }

                    _logger.LogInformation("好友列表更新完成，当前好友数: {0}", _friends.Count);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"更新好友列表失败: {ex.Message}\nStackTrace: {ex.StackTrace}");
            }
            finally
            {
                _updateLock.Release();
            }
        }

        private void UpdateViewModel(FriendItemViewModel viewModel, Dictionary<string, object> friend)
        {
            var uname = friend.GetValueOrDefault("username")?.ToString() ?? string.Empty;
            viewModel.Username = uname;
            viewModel.DisplayName = friend.GetValueOrDefault("name")?.ToString() ?? uname;
            viewModel.Online = Convert.ToBoolean(friend.GetValueOrDefault("online", false));
            viewModel.Unread = _link.UnreadMessages.GetValueOrDefault(uname, 0);
            viewModel.AvatarId = friend.GetValueOrDefault("avatar_id")?.ToString();

            var conv = friend.GetValueOrDefault("conversations") as Dictionary<string, object>;
            viewModel.LastMessage = conv?.GetValueOrDefault("content")?.ToString() ?? string.Empty;
            viewModel.LastMessageTime = conv?.GetValueOrDefault("last_update_time")?.ToString() ?? string.Empty;

            _logger.LogDebug("更新 ViewModel: username={0}, displayName={1}, online={2}, unread={3}, avatarId={4}",
                viewModel.Username, viewModel.DisplayName, viewModel.Online, viewModel.Unread, viewModel.AvatarId);
        }

        private async Task UpdateAvatarAsync(FriendItemViewModel viewModel)
        {
            if (string.IsNullOrEmpty(viewModel.AvatarId))
            {
                _logger.LogDebug("头像 ID 为空，跳过下载: username={0}", viewModel.Username);
                return;
            }

            string savePath = Path.Combine(_avatarDir, viewModel.AvatarId);
            try
            {
                // 检查本地文件
                if (File.Exists(savePath))
                {
                    if (new FileInfo(savePath).Length == 0)
                    {
                        File.Delete(savePath); // 删除无效文件
                    }
                    else
                    {
                        _logger.LogDebug("使用本地头像: username={0}, path={1}", viewModel.Username, savePath);
                        return; // 文件有效，无需下载
                    }
                }

                // 下载头像
                _logger.LogDebug("开始下载头像: username={0}, avatar_id={1}", viewModel.Username, viewModel.AvatarId);
                var resp = await _link.DownloadMedia(viewModel.AvatarId, savePath, "avatar");
                if (resp.GetValueOrDefault("status")?.ToString() != "success")
                {
                    _logger.LogError("头像下载失败: username={0}, error={1}", viewModel.Username, resp.GetValueOrDefault("message"));
                    if (File.Exists(savePath))
                    {
                        File.Delete(savePath); // 删除无效文件
                    }
                }
                else
                {
                    _logger.LogDebug("头像下载成功: username={0}, path={1}", viewModel.Username, savePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("更新头像失败: username={0}, avatar_id={1}, error={2}", viewModel.Username, viewModel.AvatarId, ex.Message);
                if (File.Exists(savePath))
                {
                    File.Delete(savePath);
                }
            }
        }

        private string NormalizeFriends(List<Dictionary<string, object>> friends)
        {
            var normalized = friends
                .Where(f => f != null && f.ContainsKey("username") && f["username"] != null)
                .Select(f => new Dictionary<string, object>
                {
                    { "username", f.GetValueOrDefault("username") },
                    { "name", f.GetValueOrDefault("name") },
                    { "online", f.GetValueOrDefault("online") },
                    { "avatar_id", f.GetValueOrDefault("avatar_id") ?? "" },
                    { "last_message", (f.GetValueOrDefault("conversations") as Dictionary<string, object>)?.GetValueOrDefault("content")?.ToString() ?? "" },
                    { "last_message_time", (f.GetValueOrDefault("conversations") as Dictionary<string, object>)?.GetValueOrDefault("last_update_time")?.ToString() ?? "" },
                    { "unread_count", _link.UnreadMessages.GetValueOrDefault(f.GetValueOrDefault("username")?.ToString(), 0) }
                })
                .ToList();

            return JsonConvert.SerializeObject(normalized, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        }

        private void FriendItemControl_FriendClicked(object sender, string username)
        {
            _logger.LogDebug("好友点击: username={0}", username);
            FriendSelected?.Invoke(this, username);
        }

        private class FriendItemViewModel
        {
            public string Username { get; set; }
            public string DisplayName { get; set; }
            public bool Online { get; set; }
            public int Unread { get; set; }
            public string AvatarId { get; set; }
            public string LastMessage { get; set; }
            public string LastMessageTime { get; set; }
        }
    }
}
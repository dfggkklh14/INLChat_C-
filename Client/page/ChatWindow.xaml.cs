using System;
using System.Collections.Generic;
using System.Windows;
using Client.Function;
using Client.Utility;
using Microsoft.Extensions.Logging;

namespace Client.page
{
    public partial class ChatWindow : Window
    {
        private readonly App _app;
        private readonly Link _chatClient;
        private readonly ILogger<ChatWindow> _logger;

        public ChatWindow(App app, Link chatClient)
        {
            _logger = app.LogFactory.CreateLogger<ChatWindow>();
            _logger.LogDebug("ChatWindow 构造函数开始");

            if (chatClient == null)
            {
                _logger.LogError("chatClient 参数为 null");
                throw new ArgumentNullException(nameof(chatClient));
            }

            _app = app;
            _chatClient = chatClient;
            InitializeComponent();
            // 初始化 FriendListControl
            if (FriendList == null)
            {
                FriendList = new FriendListControl(_chatClient, app.LogFactory);
                _logger.LogDebug("FriendListControl 初始化完成");
            }
            _chatClient.FriendListUpdated += OnFriendListUpdated;
            _logger.LogDebug("ChatWindow 构造函数完成");
        }

        private void OnFriendListUpdated(List<Dictionary<string, object>> friends)
        {
            Dispatcher.Invoke(() =>
            {
                if (friends == null)
                {
                    _logger.LogWarning("FriendListUpdated 收到 null 朋友列表");
                    return;
                }
                _logger.LogInformation("收到 FriendListUpdated 事件，朋友列表长度: {0}", friends.Count);
                foreach (var friend in friends)
                {
                    _logger.LogDebug("好友数据: username={0}, name={1}, online={2}, avatar_id={3}",
                        friend.GetValueOrDefault("username")?.ToString() ?? "null",
                        friend.GetValueOrDefault("name")?.ToString() ?? "null",
                        friend.GetValueOrDefault("online")?.ToString() ?? "null",
                        friend.GetValueOrDefault("avatar_id")?.ToString() ?? "null");
                }
                // 委托给 FriendListControl 更新好友列表
                try
                {
                    FriendList.UpdateFriendList(friends);
                    _logger.LogDebug("已调用 FriendList.UpdateFriendList");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"调用 FriendList.UpdateFriendList 失败: {ex.Message}\nStackTrace: {ex.StackTrace}");
                }
            });
        }

        private void FriendList_FriendSelected(object sender, string username)
        {
            _logger.LogInformation("选择好友: {0}", username);
            // TODO: 实现好友选择逻辑，例如显示聊天记录
            MessageBox.Show($"Selected friend: {username}", "Friend Selected", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public async void Logout()
        {
            var result = MessageBox.Show("您确定要退出当前登录吗？", "退出登录", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.No)
                return;

            try
            {
                await _chatClient.Logout();
                _logger.LogInformation("注销成功");
                _app.ShowLoginWindow();
            }
            catch (Exception ex)
            {
                _logger.LogError($"注销失败: {ex.Message}");
                MessageBox.Show($"注销失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _logger.LogDebug("ChatWindow 关闭");
            _chatClient.FriendListUpdated -= OnFriendListUpdated;
            e.Cancel = true; // 取消关闭，隐藏窗口
            Hide();
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _chatClient?.Dispose();
            base.OnClosed(e);
        }
    }
}
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Client.page;
using Client.Utility.FriendList;
using Client.Function;
using System.Windows;

namespace Client.Utility
{
    public partial class ChatArea : UserControl
    {
        private readonly ILogger<ChatArea> _logger;

        public ChatArea()
        {
            InitializeComponent();
            var app = Application.Current as App;
            _logger = app?.LogFactory?.CreateLogger<ChatArea>() ?? NullLogger<ChatArea>.Instance;
            _logger.LogDebug("ChatArea 初始化完成");
            this.IsVisibleChanged += (s, e) => _logger.LogDebug($"ChatArea Visibility 变化: {this.Visibility}");
            this.KeyDown += ChatArea_KeyDown;
        }

        public void UpdateFriendInfo(FriendModel friend)
        {
            _logger.LogDebug($"更新好友信息: Name={friend?.Name}, Online={friend?.Online}");
            DataContext = friend;
        }

        public void ClearFriendInfo()
        {
            _logger.LogDebug("清空好友信息");
            DataContext = null;
        }

        private void ChatArea_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                _logger.LogDebug("ChatArea 检测到 Esc 键，取消选择好友");
                var chatWindow = Window.GetWindow(this) as ChatWindow;
                if (chatWindow?.FriendList is FriendListControl friendListControl)
                {
                    friendListControl.FriendListBox.SelectedItem = null;
                    _logger.LogDebug("已通知 FriendListControl 取消选择");
                }
                else
                {
                    _logger.LogError("无法找到 ChatWindow 或 FriendListControl");
                }
                e.Handled = true;
            }
        }

        private void MessageInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                _logger.LogDebug("StyleTextBox 检测到 Esc 键，取消选择好友");
                var chatWindow = Window.GetWindow(this) as ChatWindow;
                if (chatWindow?.FriendList is FriendListControl friendListControl)
                {
                    friendListControl.FriendListBox.SelectedItem = null;
                    _logger.LogDebug("已通知 FriendListControl 取消选择（从 StyleTextBox）");
                }
                else
                {
                    _logger.LogError("无法找到 ChatWindow 或 FriendListControl（从 StyleTextBox）");
                }
                e.Handled = true;
            }
        }
    }
}
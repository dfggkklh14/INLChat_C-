using Client.Function;
using Client.Utility.FriendList;
using Microsoft.Extensions.Logging;
using System;
using System.Windows;

namespace Client.page
{
    public partial class ChatWindow : Window
    {
        private readonly App _app;
        private readonly Link _chatClient;
        private readonly ILogger<ChatWindow> _logger;

        public Link ChatClient => _chatClient; // 用于 XAML 绑定

        public ChatWindow(App app, Link chatClient)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
            if (_app.LogFactory == null)
            {
                throw new InvalidOperationException("App.LogFactory 未初始化");
            }
            _logger = _app.LogFactory.CreateLogger<ChatWindow>();
            _logger.LogDebug("ChatWindow 构造函数开始");

            InitializeComponent();

            DataContext = this;
            Loaded += ChatWindow_Loaded;
            _logger.LogDebug("ChatWindow 构造函数完成");
        }

        private void ChatWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _logger.LogDebug("ChatWindow_Loaded 执行");
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
            e.Cancel = true;
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
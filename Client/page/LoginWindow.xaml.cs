using Client.Function;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Client.page
{
    public partial class LoginWindow : Window
    {
        private readonly App _app;
        private readonly Link _chatClient;
        private readonly ILogger<LoginWindow> _logger;

        public LoginWindow(App app, Link chatClient)
        {
            InitializeComponent();
            _app = app;
            _chatClient = chatClient;
            _logger = app.LogFactory.CreateLogger<LoginWindow>();
            _logger.LogInformation("LoginWindow 初始化完成");

            login_button.Click += OnLogin;
            register_label.MouseLeftButtonUp += Register_label_MouseLeftButtonUp;
        }

        private async void OnLogin(object sender, RoutedEventArgs e)
        {
            string username = username_input.Text?.Trim();
            string password = password_input.Password?.Trim();

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                System.Windows.MessageBox.Show("账号或密码不能为空", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            login_button.Content = "正在连接服务器";
            login_button.IsEnabled = false;

            try
            {
                await AsyncLogin(username, password);
            }
            finally
            {
                login_button.Content = "登录";
                login_button.IsEnabled = true;
            }
        }

        private async Task AsyncLogin(string username, string password)
        {
            _logger.LogDebug($"开始登录尝试: username={username}");
            try
            {
                await _chatClient.Connect();
                _logger.LogInformation("连接服务器成功");
            }
            catch (Exception ex)
            {
                string errorMsg = "未连接到服务器";
                _logger.LogError($"{errorMsg}: {ex.Message}");
                await Dispatcher.InvokeAsync(() =>
                {
                    System.Windows.MessageBox.Show(errorMsg, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                });
                return;
            }

            try
            {
                string result = await _chatClient.Authenticate(username, password);
                _logger.LogInformation($"认证结果: {result}");

                if (result == "认证成功")
                {
                    await _chatClient.Start();
                    _logger.LogDebug("客户端消息读取线程已启动");

                    await Dispatcher.InvokeAsync(() =>
                    {
                        _app.ShowChatWindow();
                        _logger.LogDebug("准备关闭 LoginWindow");
                        // 延迟关闭以确保 ChatWindow 和 FriendListControl 初始化完成
                        Dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                Close();
                                _logger.LogDebug("LoginWindow 已关闭");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError($"关闭 LoginWindow 失败: {ex.Message}\nStackTrace: {ex.StackTrace}");
                            }
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    });
                }
                else
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        System.Windows.MessageBox.Show($"登录失败: {result}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"认证过程中发生异常: {ex.Message}\nStackTrace: {ex.StackTrace}");
                await Dispatcher.InvokeAsync(() =>
                {
                    System.Windows.MessageBox.Show($"登录失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                });
                try
                {
                    await _chatClient.CloseConnection();
                }
                catch (Exception closeEx)
                {
                    _logger.LogError($"关闭连接时出错: {closeEx.Message}");
                }
            }
        }

        private async void Register_label_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            register_label.IsEnabled = false;
            try
            {
                _logger.LogDebug("点击注册按钮，尝试关闭现有连接");
                Task.Run(async () =>
                {
                    await _chatClient.CloseConnection();
                    _logger.LogDebug("现有连接已成功关闭");
                });

                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        _logger.LogDebug("尝试显示 RegisterWindow");
                        _app.ShowRegisterWindow();
                        _logger.LogDebug("ShowRegisterWindow 调用完成");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"显示 RegisterWindow 失败: {ex.Message}\nStackTrace: {ex.StackTrace}");
                        System.Windows.MessageBox.Show("无法打开注册窗口，请重试", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"关闭连接时出错: {ex.Message}\nStackTrace: {ex.StackTrace}");
                await Dispatcher.InvokeAsync(() =>
                {
                    System.Windows.MessageBox.Show("连接清理失败，请重试", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
            finally
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    register_label.IsEnabled = true;
                    _logger.LogDebug("恢复 register_label 可用性");
                });
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            _logger.LogDebug("LoginWindow 正在关闭");
        }
    }
}
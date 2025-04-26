using System;
using System.IO;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using System.Windows.Controls;
using Client.page;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using Client.Function;

namespace Client
{
    public partial class App : Application
    {
        private readonly TaskbarIcon _trayIcon;
        private readonly ILogger<App> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private LoginWindow? _loginWindow;
        private RegisterWindow? _registerWindow;
        private ChatWindow? _chatWindow;
        private Link? _chatClient;
        private bool _isLoggedIn;

        public App()
        {
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            _loggerFactory = LoggerFactory.Create(builder =>
            {
                builder
                    .AddConsole(options =>
                    {
                        options.FormatterName = "CustomTimestampFormatter";
                    })
                    .AddConsoleFormatter<CustomTimestampFormatter, ConsoleFormatterOptions>()
                    .AddProvider(new DebugLoggerProvider())
                    .SetMinimumLevel(LogLevel.Debug);
            });
            _logger = _loggerFactory.CreateLogger<App>();
            _logger.LogInformation("App LogFactory 初始化成功");
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // 初始化系统托盘图标
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon", "icon.ico");
            _trayIcon = new TaskbarIcon();
            try
            {
                if (!File.Exists(iconPath))
                {
                    _logger.LogError($"托盘图标文件未找到: {iconPath}");
                    _trayIcon.ToolTipText = "ChatINL";
                }
                else
                {
                    _trayIcon.Icon = new System.Drawing.Icon(iconPath);
                    _trayIcon.ToolTipText = "ChatINL";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"加载托盘图标失败: {ex.Message}");
                _trayIcon.ToolTipText = "ChatINL";
            }

            SetupTrayMenu();
            InitializeChatClient();
            _loginWindow = new LoginWindow(this, _chatClient);
            _loginWindow.Closed += (s, e) => OnWindowClosed(_loginWindow);
        }

        public class CustomTimestampFormatter : ConsoleFormatter
        {
            private readonly ConsoleFormatterOptions _options;

            public CustomTimestampFormatter(IOptions<ConsoleFormatterOptions> options)
                : base("CustomTimestampFormatter")
            {
                _options = options.Value;
            }

            public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider scopeProvider, TextWriter textWriter)
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string message = logEntry.Formatter(logEntry.State, logEntry.Exception);
                string logLevel = logEntry.LogLevel.ToString();
                textWriter.WriteLine($"[{timestamp}] [{logLevel}] {message}");
                if (logEntry.Exception != null)
                {
                    textWriter.WriteLine(logEntry.Exception.ToString());
                }
            }
        }

        public class DebugLoggerProvider : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName)
            {
                return new DebugLogger(categoryName);
            }

            public void Dispose() { }
        }

        public class DebugLogger : ILogger
        {
            private readonly string _categoryName;

            public DebugLogger(string categoryName)
            {
                _categoryName = categoryName;
            }

            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (!IsEnabled(logLevel))
                    return;

                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string message = formatter(state, exception);
                string logLine = $"[{timestamp}] [{logLevel}] {_categoryName}: {message}";
                Debug.WriteLine(logLine);
                if (exception != null)
                {
                    Debug.WriteLine(exception.ToString());
                }
            }
        }

        private class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new NullScope();
            public void Dispose() { }
        }

        public ILoggerFactory LogFactory => _loggerFactory;

        private void InitializeChatClient()
        {
            if (_chatClient != null)
            {
                try
                {
                    _chatClient.CloseConnection().GetAwaiter().GetResult();
                    _chatClient.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"清理旧 ChatClient 失败: {ex.Message}");
                }
            }
            _chatClient = new Link(_loggerFactory);
            _logger.LogInformation("已初始化新的 ChatClient");
        }

        public void ShowRegisterWindow()
        {
            if (_registerWindow == null || !_registerWindow.IsVisible)
            {
                InitializeChatClient();
                _registerWindow = new RegisterWindow(this, _chatClient);
                _registerWindow.Closed += (s, e) => OnWindowClosed(_registerWindow);
                _loginWindow?.Close();
                _registerWindow.Show();
                _logger.LogInformation("显示 RegisterWindow");
            }
            else
            {
                _registerWindow.Activate();
            }
        }

        public void ShowChatWindow()
        {
            if (_chatWindow == null || !_chatWindow.IsVisible)
            {
                _chatWindow = new ChatWindow(this, _chatClient);
                _chatWindow.Closed += (s, e) => OnWindowClosed(_chatWindow);
                _loginWindow?.Close();
                _chatWindow.Show();
                _isLoggedIn = true;
                _logger.LogInformation("显示 ChatWindow");
            }
            else
            {
                _chatWindow.Activate();
            }
        }

        public void ShowLoginWindow()
        {
            try
            {
                _logger.LogDebug("ShowLoginWindow 开始执行");
                if (_loginWindow == null || !_loginWindow.IsVisible)
                {
                    _logger.LogDebug("创建新的 LoginWindow");
                    InitializeChatClient();
                    _loginWindow = new LoginWindow(this, _chatClient);
                    _loginWindow.Closed += (s, e) => OnWindowClosed(_loginWindow);
                    _loginWindow.Show();
                    _isLoggedIn = false;
                    _logger.LogInformation("显示 LoginWindow");
                }
                else
                {
                    _logger.LogDebug("激活已有 LoginWindow");
                    _loginWindow.Activate();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"显示 LoginWindow 失败: {ex.Message}\n堆栈跟踪: {ex.StackTrace}");
                MessageBox.Show("无法打开登录窗口，请重启应用程序", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SetupTrayMenu()
        {
            var contextMenu = new ContextMenu();
            var openMenuItem = new MenuItem { Header = "打开主界面" };
            openMenuItem.Click += (s, e) => ShowActiveWindow();
            var exitMenuItem = new MenuItem { Header = "退出" };
            exitMenuItem.Click += async (s, e) =>
            {
                _logger.LogDebug("系统托盘退出选项被点击");
                try
                {
                    if (_chatClient != null)
                    {
                        await _chatClient.CloseConnection();
                    }
                    Shutdown();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"系统托盘退出失败: {ex.Message}");
                    MessageBox.Show($"退出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            contextMenu.Items.Add(openMenuItem);
            contextMenu.Items.Add(exitMenuItem);
            _trayIcon.ContextMenu = contextMenu;
            _trayIcon.TrayMouseDoubleClick += (s, e) => ShowActiveWindow();
        }

        private void ShowActiveWindow()
        {
            if (_isLoggedIn && _chatWindow != null)
            {
                if (!_chatWindow.IsVisible)
                {
                    _chatWindow.Show();
                    _logger.LogInformation("重新显示隐藏的 ChatWindow");
                }
                _chatWindow.Activate();
            }
            else
            {
                ShowLoginWindow();
            }
        }

        private void OnWindowClosed(Window? window)
        {
            try
            {
                _logger.LogDebug($"OnWindowClosed: 窗口 {window?.GetType().Name} 关闭");
                if (window == _loginWindow)
                {
                    _loginWindow = null;
                    _logger.LogDebug("LoginWindow 已置空");
                }
                else if (window == _registerWindow)
                {
                    _registerWindow = null;
                    _logger.LogDebug("RegisterWindow 已置空，调用 ShowLoginWindow");
                    ShowLoginWindow();
                }
                else if (window == _chatWindow)
                {
                    _chatWindow = null;
                    _logger.LogDebug("ChatWindow 已置空");
                }

                if (_loginWindow == null && _registerWindow == null && _chatWindow == null)
                {
                    _logger.LogInformation("所有窗口已关闭，退出应用程序");
                    Shutdown();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"处理窗口关闭时出错: {ex.Message}\n堆栈跟踪: {ex.StackTrace}");
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            _logger.LogInformation("应用程序启动");
            _loginWindow?.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _logger.LogInformation("应用程序开始关闭");
            try
            {
                if (_chatClient != null)
                {
                    _chatClient.CloseConnection().GetAwaiter().GetResult();
                    _chatClient.Dispose();
                    _chatClient = null;
                }

                if (_loginWindow != null && _loginWindow.IsVisible)
                {
                    _loginWindow.Close();
                }
                if (_registerWindow != null && _registerWindow.IsVisible)
                {
                    _registerWindow.Close();
                }
                if (_chatWindow != null && _chatWindow.IsVisible)
                {
                    _chatWindow.Close();
                }

                _trayIcon?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError($"关闭应用程序时出错: {ex.Message}");
            }
            base.OnExit(e);
        }
        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            _logger.LogError($"未处理异常: {e.Exception}");
            e.Handled = true; // 防止程序崩溃
            MessageBox.Show($"发生错误：{e.Exception.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
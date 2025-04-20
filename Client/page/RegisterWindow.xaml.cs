using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Client.Function;
using Client.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace Client.page
{
    public partial class RegisterWindow : Window
    {
        private readonly App _app;
        private readonly Link _chatClient;
        private string _userId;
        private string _sessionId;
        private string _localAvatarPath;
        private readonly ILogger<RegisterWindow> _logger;

        public RegisterWindow(App app, Link chatClient)
        {
            InitializeComponent();
            _app = app;
            _chatClient = chatClient;
            _logger = app.LogFactory.CreateLogger<RegisterWindow>();
            _userId = null;
            AvatarWidget.UploadCallback = OnAvatarUploaded;

            // 异步检查并建立连接
            Task.Run(async () =>
            {
                try
                {
                    await _chatClient.Connect();
                    _logger.LogInformation("RegisterWindow 初始化时成功连接到服务器");
                    await Dispatcher.InvokeAsync(() => StartRegisterProcess());
                }
                catch (Exception ex)
                {
                    _logger.LogError($"RegisterWindow 初始化连接失败: {ex.Message}");
                    await Dispatcher.InvokeAsync(() => ShowFloatingLabel("无法连接到服务器，请检查网络后重试"));
                }
            });
        }

        private void IdLabel_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            ((System.Windows.Controls.Label)sender).Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 163, 108));
        }

        private void IdLabel_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            ((System.Windows.Controls.Label)sender).Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(128, 128, 128));
        }

        private void IdLabel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_userId == null)
            {
                ShowFloatingLabel("ID 未生成，无法复制");
                return;
            }

            try
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        EmptyClipboard();
                        IntPtr hGlobal = Marshal.StringToHGlobalUni(_userId);
                        try
                        {
                            SetClipboardData(CF_UNICODETEXT, hGlobal);
                            ShowFloatingLabel("ID 已复制到剪贴板");
                        }
                        catch
                        {
                            Marshal.FreeHGlobal(hGlobal);
                            throw;
                        }
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                }
                else
                {
                    _logger.LogWarning("无法打开剪贴板 (Win32)");
                    ShowFloatingLabel($"复制失败，请手动复制 ID: {_userId}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"复制 ID 失败: {ex.Message}\n堆栈跟踪: {ex.StackTrace}");
                ShowFloatingLabel($"复制失败，请手动复制 ID: {_userId}");
            }
        }

        // Win32 API 声明
        private const uint CF_UNICODETEXT = 13;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        private void ImageVerifyLabel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            RefreshCaptcha();
        }

        private void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            SubmitRegistration();
        }

        private async void StartRegisterProcess()
        {
            try
            {
                var resp = await _chatClient.Register("register_1");
                if (resp.GetValueOrDefault("status")?.ToString() == "success")
                {
                    _userId = resp.GetValueOrDefault("username")?.ToString();
                    _sessionId = resp.GetValueOrDefault("session_id")?.ToString();
                    IdLabel.Content = $"ID:{_userId}";
                    var captchaImg = Convert.FromBase64String(resp.GetValueOrDefault("captcha_image")?.ToString());
                    var pixmap = new BitmapImage();
                    using (var ms = new MemoryStream(captchaImg))
                    {
                        pixmap.BeginInit();
                        pixmap.StreamSource = ms;
                        pixmap.CacheOption = BitmapCacheOption.OnLoad;
                        pixmap.EndInit();
                    }
                    pixmap.Freeze();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        ImageVerifyLabel.Source = pixmap;
                    });
                }
                else
                {
                    ShowFloatingLabel(resp.GetValueOrDefault("message")?.ToString() ?? "无法连接到服务器");
                }
            }
            catch (Exception ex)
            {
                ShowFloatingLabel("无法连接到服务器，请检查网络后重试");
                _logger.LogError($"启动注册过程错误：{ex.Message}\n堆栈跟踪：{ex.StackTrace}");
            }
        }

        private async void RefreshCaptcha()
        {
            if (_sessionId == null)
            {
                ShowFloatingLabel("请先获取初始验证码");
                return;
            }
            try
            {
                var resp = await _chatClient.Register("register_4", sessionId: _sessionId);
                _logger.LogDebug($"刷新验证码响应：{JsonConvert.SerializeObject(resp)}");
                if (resp.GetValueOrDefault("status")?.ToString() == "success")
                {
                    var captchaImgBase64 = resp.GetValueOrDefault("captcha_image")?.ToString();
                    if (string.IsNullOrEmpty(captchaImgBase64))
                    {
                        ShowFloatingLabel("服务器未返回验证码图片");
                        return;
                    }
                    var captchaImg = Convert.FromBase64String(captchaImgBase64);
                    File.WriteAllBytes("captcha_refresh_debug.png", captchaImg);
                    _logger.LogError("刷新的验证码已保存至captcha_refresh_debug.png");
                    var pixmap = new BitmapImage();
                    using (var ms = new MemoryStream(captchaImg))
                    {
                        pixmap.BeginInit();
                        pixmap.StreamSource = ms;
                        pixmap.CacheOption = BitmapCacheOption.OnLoad;
                        pixmap.EndInit();
                    }
                    pixmap.Freeze();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        ImageVerifyLabel.Source = pixmap;
                        _logger.LogDebug($"图像验证标签源已更新，可见性：{ImageVerifyLabel.Visibility}");
                    });
                }
                else
                {
                    ShowFloatingLabel(resp.GetValueOrDefault("message")?.ToString() ?? "刷新验证码失败");
                }
            }
            catch (Exception ex)
            {
                ShowFloatingLabel("刷新验证码失败");
                _logger.LogDebug($"刷新验证码错误：{ex.Message}\nStackTrace：{ex.StackTrace}");
            }
        }

        private async void SubmitRegistration()
        {
            var captchaInput = InputVerify.Text.Trim();
            var password = InputPassword.Password.Trim();
            var confirmPassword = SecondInputPassword.Password.Trim();
            var nickname = InputName.Text.Trim();

            if (string.IsNullOrEmpty(captchaInput) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(confirmPassword))
            {
                ShowFloatingLabel("请填写完整信息");
                return;
            }
            if (password != confirmPassword)
            {
                ShowFloatingLabel("两次密码不一致");
                return;
            }

            try
            {
                var resp = await _chatClient.Register("register_2", sessionId: _sessionId, captchaInput: captchaInput);
                _logger.LogDebug($"Register_2 响应: {JsonConvert.SerializeObject(resp)}");
                if (resp.GetValueOrDefault("status")?.ToString() != "success")
                {
                    ShowFloatingLabel(resp.GetValueOrDefault("message")?.ToString() ?? "验证码错误");
                    if (resp.ContainsKey("captcha_image"))
                    {
                        var captchaImg = Convert.FromBase64String(resp.GetValueOrDefault("captcha_image")?.ToString());
                        var pixmap = new BitmapImage();
                        using (var ms = new MemoryStream(captchaImg))
                        {
                            pixmap.BeginInit();
                            pixmap.StreamSource = ms;
                            pixmap.CacheOption = BitmapCacheOption.OnLoad;
                            pixmap.EndInit();
                        }
                        pixmap.Freeze();
                        await Dispatcher.InvokeAsync(() =>
                        {
                            ImageVerifyLabel.Source = pixmap;
                        });
                    }
                    return;
                }

                resp = await _chatClient.Register(
                    "register_3",
                    sessionId: _sessionId,
                    password: password,
                    avatar: _localAvatarPath != null ? System.Drawing.Image.FromFile(_localAvatarPath) : null,
                    nickname: nickname,
                    sign: ""
                );
                _logger.LogDebug($"Register_3 响应: {JsonConvert.SerializeObject(resp)}");
                if (resp.GetValueOrDefault("status")?.ToString() == "success")
                {
                    ShowFloatingLabel("注册成功");
                    await Task.Delay(1000);
                    // 移除 DialogResult = true; 并安全关闭窗口
                    await Dispatcher.InvokeAsync(() => Close());
                }
                else
                {
                    ShowFloatingLabel(resp.GetValueOrDefault("message")?.ToString() ?? "注册失败");
                }
            }
            catch (Exception ex)
            {
                ShowFloatingLabel("注册失败");
                _logger.LogDebug($"提交注册错误：{ex.Message}\nStackTrace：{ex.StackTrace}");
            }
        }

        private void OnAvatarUploaded(BitmapImage croppedImage)
        {
            var avatarImage = (System.Windows.Controls.Image)AvatarWidget.FindName("AvatarImage");
            croppedImage.Freeze();
            avatarImage.Source = croppedImage;
            using (var ms = new MemoryStream())
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(croppedImage));
                encoder.Save(ms);
                _localAvatarPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");
                File.WriteAllBytes(_localAvatarPath, ms.ToArray());
            }
        }

        private void ShowFloatingLabel(string message)
        {
            var offset = new Thickness(0, 50, 0, 0);
            var floatingLabel = new FloatingLabelControl(message, (Grid)Content, offset);
        }

        protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            try
            {
                _logger.LogDebug("正在关闭 RegisterWindow，发送退出请求。");
                await _chatClient.CloseConnection();
                _logger.LogDebug("退出请求发送成功。");
            }
            catch (Exception ex)
            {
                _logger.LogError($"发送退出请求时出错: {ex.Message}\n堆栈跟踪: {ex.StackTrace}");
            }
            finally
            {
                // 无论连接清理是否成功，都显示登录窗口
                try
                {
                    _logger.LogDebug("尝试显示 LoginWindow。");
                    _app.ShowLoginWindow();
                    _logger.LogDebug("ShowLoginWindow 调用完成。");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"显示 LoginWindow 失败: {ex.Message}\n堆栈跟踪: {ex.StackTrace}");
                }
            }
        }
    }
}
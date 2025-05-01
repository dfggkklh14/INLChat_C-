using Client.Function;
using Client.page;
using Client.Utility.FriendList;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Client.Utility.Chat
{
    public partial class ChatArea : UserControl
    {
        private readonly ILogger<ChatArea> _logger;
        private Link _link;
        private int _currentPage = 1;
        private bool _isLoadingPage = false;
        private const int PageSize = 20;
        private ObservableCollection<ChatHistoryEntry> _chatEntries = new ObservableCollection<ChatHistoryEntry>();
        private readonly string _cachePath;
        private bool _isInitialized = false;


        public ChatArea()
        {
            InitializeComponent();
            ChatAreaList.ItemsSource = _chatEntries;
            _cachePath = GetCachePath();

            var app = Application.Current as App;
            _logger = app?.LogFactory?.CreateLogger<ChatArea>() ?? NullLogger<ChatArea>.Instance;
            _logger.LogDebug("ChatArea 初始化完成");
            this.IsVisibleChanged += (s, e) => _logger.LogDebug($"ChatArea Visibility 变化: {this.Visibility}");
            this.KeyDown += ChatArea_KeyDown;

            // 延迟初始化完成标志
            Loaded += async (s, e) =>
            {
                await Task.Delay(100); // 等待渲染完成
                _isInitialized = true;
                _logger.LogDebug("ChatArea 渲染完成，初始化标志设置为 true");
            };
        }

        public void UpdateFriendInfo(FriendModel friend, Link link = null)
        {
            _logger.LogDebug($"更新好友信息: Name={friend?.Name}, Online={friend?.Online}, AvatarId={friend?.AvatarId}");
            DataContext = friend;
            _link = link;
            _currentPage = 1;
            _chatEntries.Clear();

            if (friend != null && link != null)
            {
                var avatarControl = FindVisualChild<FriendAvatarControl>(this);
                if (avatarControl != null)
                {
                    avatarControl.Link = link;
                    _link.ThumbnailDownloaded -= OnThumbnailDownloaded;
                    _logger.LogDebug("已为 FriendAvatarControl 设置 Link");
                }
                else
                {
                    _logger.LogWarning("未找到 FriendAvatarControl");
                }
                _link = link;
                if (_link != null)
                {
                    _link.ThumbnailDownloaded += OnThumbnailDownloaded; // 订阅事件
                }

                link.NewMessageReceived += OnNewMessageReceived;
                link.NewMediaReceived += OnNewMediaReceived;
                _logger.LogDebug("已订阅 NewMessageReceived 和 NewMediaReceived 事件");

                LoadInitialChatHistory(friend.Username);
            }
        }

        private void OnThumbnailDownloaded(string fileId, string savePath)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var entry = _chatEntries.FirstOrDefault(e => e.FileId == fileId);
                if (entry != null)
                {
                    entry.ThumbnailLocalPath = savePath;
                    _logger.LogDebug($"更新缩略图路径: FileId={fileId}");
                }
            });
        }

        // 生成缩略图
        private string GenerateThumbnail(string originalImagePath, string fileId)
        {
            try
            {
                string thumbnailDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Chat_DATA", "thumbnails");
                Directory.CreateDirectory(thumbnailDir);
                string thumbnailPath = Path.Combine(thumbnailDir, $"{fileId}_thumbnail.jpg");

                using (System.Drawing.Image image = System.Drawing.Image.FromFile(originalImagePath))
                {
                    int originalWidth = image.Width;
                    int originalHeight = image.Height;

                    // 计算缩放比例（短边变成 200）
                    double scale = originalWidth < originalHeight
                        ? 200.0 / originalWidth
                        : 200.0 / originalHeight;

                    int targetWidth = (int)(originalWidth * scale);
                    int targetHeight = (int)(originalHeight * scale);

                    using (Bitmap thumbnail = new Bitmap(targetWidth, targetHeight))
                    {
                        using (Graphics g = Graphics.FromImage(thumbnail))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

                            g.DrawImage(image, 0, 0, targetWidth, targetHeight);
                        }

                        thumbnail.Save(thumbnailPath, ImageFormat.Jpeg);
                    }
                }

                _logger.LogDebug($"缩略图生成成功: {thumbnailPath}");
                return thumbnailPath;
            }
            catch (Exception ex)
            {
                _logger.LogError($"生成缩略图失败: {ex.Message}");
                return null;
            }
        }

        // 发送图片
        public async Task SendImageAsync(string imagePath)
        {
            if (_link == null || DataContext == null)
            {
                _logger.LogWarning("无法发送图片：未初始化 Link 或未选择好友");
                MessageBox.Show("请先选择好友", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var friend = DataContext as FriendModel;
            if (friend == null)
            {
                _logger.LogWarning("DataContext 不是 FriendModel，无法发送图片");
                MessageBox.Show("好友信息无效", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                _logger.LogDebug($"发送图片到 {friend.Username}: {imagePath}");
                string fileType = "image";
                var response = await _link.SendMedia(friend.Username, imagePath, fileType, null, "", (state, progress, fileName) =>
                {
                    _logger.LogDebug($"上传进度: {state}, {progress:F2}%, 文件: {fileName}");
                });

                if (response.GetValueOrDefault("status")?.ToString() == "success")
                {
                    _logger.LogDebug("图片发送成功");
                    string fileId = response.GetValueOrDefault("file_id")?.ToString();
                    if (string.IsNullOrEmpty(fileId))
                    {
                        _logger.LogWarning("响应中缺少 file_id");
                        MessageBox.Show("服务器响应缺少文件 ID", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    string thumbnailPath = GenerateThumbnail(imagePath, fileId);
                    if (thumbnailPath == null)
                    {
                        _logger.LogWarning("缩略图生成失败");
                        MessageBox.Show("无法生成缩略图", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    var newEntry = new ChatHistoryEntry
                    {
                        Rowid = response.ContainsKey("rowid") ? Convert.ToInt32(response["rowid"]) : 0,
                        WriteTime = response.GetValueOrDefault("write_time")?.ToString() ?? DateTime.Now.ToString(),
                        SenderUsername = _link._username,
                        Message = response.GetValueOrDefault("message")?.ToString() ?? "",
                        IsCurrentUser = true,
                        ReplyTo = null,
                        ReplyPreview = null,
                        AttachmentType = "image",
                        FileId = fileId,
                        OriginalFileName = Path.GetFileName(imagePath),
                        ThumbnailLocalPath = thumbnailPath,
                        FileSize = new FileInfo(imagePath).Length,
                        Duration = null
                    };

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (newEntry.Rowid == 0 || !_chatEntries.Any(e => e.Rowid == newEntry.Rowid))
                        {
                            _chatEntries.Add(newEntry);
                            _logger.LogDebug($"已添加图片 ChatHistoryEntry，Rowid: {newEntry.Rowid}, FileId: {fileId}");
                            ScrollToBottom();
                        }
                        else
                        {
                            _logger.LogDebug($"图片消息 Rowid: {newEntry.Rowid} 已存在，忽略");
                        }
                    });
                }
                else
                {
                    string errorMsg = response.GetValueOrDefault("message")?.ToString() ?? "未知错误";
                    _logger.LogError($"图片发送失败: {errorMsg}");
                    MessageBox.Show($"发送失败: {errorMsg}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"发送图片时发生异常: {ex.Message}");
                MessageBox.Show($"发送图片失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 处理新接收的媒体消息
        private async void OnNewMediaReceived(Dictionary<string, object> message)
        {
            _logger.LogDebug($"收到新媒体消息: {JsonConvert.SerializeObject(message)}");

            try
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var friend = DataContext as FriendModel;
                    if (friend == null)
                    {
                        _logger.LogWarning("DataContext 不是 FriendModel，忽略新媒体消息");
                        return;
                    }

                    string sender = message.GetValueOrDefault("from")?.ToString();
                    string to = message.GetValueOrDefault("to")?.ToString();
                    string fileType = message.GetValueOrDefault("file_type")?.ToString();
                    if (fileType != "image")
                    {
                        _logger.LogDebug($"非图片消息，类型: {fileType}，忽略");
                        return;
                    }

                    if ((sender != friend.Username && to != friend.Username) &&
                        (sender != _link._username && to != _link._username))
                    {
                        _logger.LogDebug($"媒体消息来自 {sender}，发送给 {to}，非当前好友 {friend.Username} 或用户 {_link._username}，忽略");
                        return;
                    }

                    int rowid = message.ContainsKey("rowid") ? Convert.ToInt32(message["rowid"]) : 0;
                    if (rowid != 0 && _chatEntries.Any(e => e.Rowid == rowid))
                    {
                        _logger.LogDebug($"媒体消息 Rowid: {rowid} 已存在，忽略");
                        return;
                    }

                    string fileId = message.GetValueOrDefault("file_id")?.ToString();
                    if (string.IsNullOrEmpty(fileId))
                    {
                        _logger.LogWarning("媒体消息缺少 file_id，忽略");
                        return;
                    }

                    string thumbnailPath = FindThumbnailPath(fileId);

                    var entry = new ChatHistoryEntry
                    {
                        Rowid = rowid,
                        WriteTime = message.GetValueOrDefault("write_time")?.ToString() ?? DateTime.Now.ToString(),
                        SenderUsername = sender ?? "unknown",
                        Message = message.GetValueOrDefault("message")?.ToString() ?? "",
                        IsCurrentUser = sender == _link._username,
                        ReplyTo = message.ContainsKey("reply_to") && message["reply_to"] != null ? Convert.ToInt32(message["reply_to"]) : (int?)null,
                        ReplyPreview = message.GetValueOrDefault("reply_preview")?.ToString(),
                        AttachmentType = "image",
                        FileId = fileId,
                        OriginalFileName = message.GetValueOrDefault("original_file_name")?.ToString(),
                        ThumbnailLocalPath = thumbnailPath,
                        FileSize = message.ContainsKey("file_size") ? Convert.ToInt64(message["file_size"]) : (long?)null,
                        Duration = null
                    };

                    _chatEntries.Add(entry);
                    _logger.LogDebug($"已添加新图片 ChatHistoryEntry，Rowid: {entry.Rowid}, FileId: {fileId}");
                    ScrollToBottom();
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"处理新媒体消息失败: {ex.Message}\nStackTrace: {ex.StackTrace}");
            }
        }

        // SendImgButton 点击事件
        private async void SendImageButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "图片文件 (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png",
                Title = "选择图片"
            };
            if (openFileDialog.ShowDialog() == true)
            {
                await SendImageAsync(openFileDialog.FileName);
            }
        }

        public void ClearFriendInfo()
        {
            _logger.LogDebug("清空好友信息");

            // 取消订阅 NewMessageReceived 事件
            if (_link != null)
            {
                _link.NewMessageReceived -= OnNewMessageReceived;
                _logger.LogDebug("已取消订阅 NewMessageReceived 事件");
            }

            DataContext = null;
            _link = null;
            _currentPage = 1;
            _chatEntries.Clear();

            var avatarControl = FindVisualChild<FriendAvatarControl>(this);
            if (avatarControl != null)
            {
                avatarControl.Link = null;
                _logger.LogDebug("已清除 FriendAvatarControl 的 Link");
            }
            _logger.LogDebug("已清空聊天记录集合");
        }

        // 加载初始聊天记录
        private async void LoadInitialChatHistory(string friendUsername)
        {
            var chatHistory = await LoadChatHistoryPageAsync(friendUsername, _currentPage);

            if (chatHistory != null && chatHistory.Count > 0)
            {
                UpdateChatHistory(chatHistory, insertAtTop: false);
                _logger.LogDebug($"已加载 {chatHistory.Count} 条初始聊天记录");
            }
            else
            {
                _logger.LogDebug("无初始聊天记录");
            }
        }


        // 处理新消息事件
        private void OnNewMessageReceived(Dictionary<string, object> message)
        {
            _logger.LogDebug($"收到新消息: {JsonConvert.SerializeObject(message)}, 线程: {System.Threading.Thread.CurrentThread.ManagedThreadId}");

            try
            {
                // 将所有 UI 相关操作移到 Dispatcher
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var friend = DataContext as FriendModel;
                    if (friend == null)
                    {
                        _logger.LogWarning("DataContext 不是 FriendModel，忽略新消息");
                        return;
                    }

                    // 检查消息是否与当前好友相关
                    string sender = message.GetValueOrDefault("from")?.ToString();
                    string to = message.GetValueOrDefault("to")?.ToString();
                    _logger.LogDebug($"消息过滤检查: sender={sender}, to={to}, friend={friend.Username}, user={_link._username}");

                    if ((sender != friend.Username && to != friend.Username) &&
                        (sender != _link._username && to != _link._username))
                    {
                        _logger.LogDebug($"新消息来自 {sender}，发送给 {to}，非当前好友 {friend.Username} 或用户 {_link._username}，忽略");
                        return;
                    }

                    // 检查是否重复消息
                    int rowid = message.ContainsKey("rowid") ? Convert.ToInt32(message["rowid"]) : 0;
                    if (rowid != 0 && _chatEntries.Any(e => e.Rowid == rowid))
                    {
                        _logger.LogDebug($"消息 Rowid: {rowid} 已存在，忽略");
                        return;
                    }

                    // 转换为 ChatHistoryEntry
                    var entry = new ChatHistoryEntry
                    {
                        Rowid = rowid,
                        WriteTime = message.GetValueOrDefault("write_time")?.ToString() ?? DateTime.Now.ToString(),
                        SenderUsername = sender ?? "unknown",
                        Message = message.GetValueOrDefault("message")?.ToString() ?? "",
                        IsCurrentUser = sender == _link._username,
                        ReplyTo = message.ContainsKey("reply_to") && message["reply_to"] != null ? Convert.ToInt32(message["reply_to"]) : (int?)null,
                        ReplyPreview = message.GetValueOrDefault("reply_preview")?.ToString(),
                        AttachmentType = null // 仅处理文本消息
                    };

                    // 添加到 _chatEntries 并滚动到最新消息
                    _chatEntries.Add(entry);
                    _logger.LogDebug($"已添加新 ChatHistoryEntry，Rowid: {entry.Rowid}, 消息: {entry.Message}");
                    ScrollToBottom();
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"处理新消息失败: {ex.Message}\nStackTrace: {ex.StackTrace}");
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await SendMessageAsync();
        }

        private async void MessageInput_KeyDown(object sender, KeyEventArgs e)
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
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
            {
                // 仅在按下 Enter 且不按 Shift 时发送消息
                await SendMessageAsync();
                e.Handled = true;
            }
        }

        private async Task SendMessageAsync()
        {
            if (_link == null || DataContext == null)
            {
                _logger.LogWarning("无法发送消息：未初始化 Link 或未选择好友");
                return;
            }

            var friend = DataContext as FriendModel;
            if (friend == null)
            {
                _logger.LogWarning("DataContext 不是 FriendModel，无法发送消息");
                return;
            }

            string message = MessageInput.Text?.Trim();
            if (string.IsNullOrEmpty(message))
            {
                _logger.LogDebug("消息为空，忽略发送");
                return;
            }

            try
            {
                _logger.LogDebug($"发送消息到 {friend.Username}: {message}");
                var response = await _link.SendMessage(friend.Username, message);

                if (response.GetValueOrDefault("status")?.ToString() == "success")
                {
                    _logger.LogDebug("消息发送成功");
                    // 清空输入框
                    MessageInput.Text = string.Empty;

                    // 手动添加消息到 _chatEntries
                    var newEntry = new ChatHistoryEntry
                    {
                        Rowid = response.ContainsKey("rowid") ? Convert.ToInt32(response["rowid"]) : 0,
                        WriteTime = response.GetValueOrDefault("write_time")?.ToString() ?? DateTime.Now.ToString(),
                        SenderUsername = _link._username,
                        Message = message,
                        IsCurrentUser = true,
                        ReplyTo = null,
                        ReplyPreview = null,
                        AttachmentType = null
                    };

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        // 检查是否重复
                        if (newEntry.Rowid == 0 || !_chatEntries.Any(e => e.Rowid == newEntry.Rowid))
                        {
                            _chatEntries.Add(newEntry);
                            _logger.LogDebug($"已添加发送消息的 ChatHistoryEntry，Rowid: {newEntry.Rowid}, 消息: {message}");
                            ScrollToBottom();
                        }
                        else
                        {
                            _logger.LogDebug($"发送消息 Rowid: {newEntry.Rowid} 已存在，忽略");
                        }
                    });
                }
                else
                {
                    string errorMsg = response.GetValueOrDefault("message")?.ToString() ?? "未知错误";
                    _logger.LogError($"消息发送失败: {errorMsg}");
                    MessageBox.Show($"发送失败: {errorMsg}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"发送消息时发生异常: {ex.Message}");
                MessageBox.Show($"发送消息失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void UpdateChatHistory(List<ChatHistoryEntry> chatHistory, bool insertAtTop = false)
        {
            _logger.LogDebug($"更新聊天记录，记录数: {chatHistory?.Count ?? 0}, 插入到顶部: {insertAtTop}");
            if (chatHistory == null || chatHistory.Count == 0)
            {
                _logger.LogDebug("聊天记录为空，不创建 ChatBubble");
                if (!insertAtTop)
                {
                    ScrollToBottom();
                }
                return;
            }

            if (!insertAtTop)
            {
                // 首次加载或刷新：清空后底部插入
                _chatEntries.Clear();
                // 为保证新消息在底部，倒序添加
                for (int i = chatHistory.Count - 1; i >= 0; i--)
                {
                    if (!_chatEntries.Any(e => e.Rowid == chatHistory[i].Rowid))
                    {
                        _chatEntries.Add(chatHistory[i]);
                    }
                }
                ScrollToBottom();
            }
            else
            {
                // 翻页加载旧消息：在开头插入
                foreach (var entry in chatHistory)
                {
                    if (!_chatEntries.Any(e => e.Rowid == entry.Rowid))
                    {
                        _chatEntries.Insert(0, entry);
                    }
                }
                _currentPage++;
            }
        }

        private void ScrollToBottom(bool force = false)
        {
            if (!force && _chatEntries.Count == 0) return;
            var scrollViewer = FindVisualChild<ScrollViewer>(ChatAreaList);
            if (scrollViewer != null)
            {
                scrollViewer.ScrollToBottom();
            }
        }

        private async void ChatAreaList_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (!_isInitialized || _isLoadingPage || _link == null || DataContext == null)
            {
                _logger.LogDebug("未初始化完成或正在加载，忽略 ScrollChanged 事件");
                return;
            }

            var scrollViewer = FindVisualChild<ScrollViewer>(ChatAreaList);
            // 只在：1. 真正滚动到 0；2. 不是因为内容变化引发的；才触发翻页
            if (scrollViewer != null
             && scrollViewer.VerticalOffset == 0
             && e.ExtentHeightChange == 0)
            {
                _isLoadingPage = true;
                double previousExtentHeight = scrollViewer.ExtentHeight;
                _logger.LogDebug($"滚动到顶部，请求第 {_currentPage + 1} 页聊天记录");

                try
                {
                    var friend = DataContext as FriendModel;
                    if (friend == null)
                    {
                        _logger.LogWarning("DataContext 不是 FriendModel，无法请求聊天记录");
                        return;
                    }

                    var chatHistory = await LoadChatHistoryPageAsync(friend.Username, _currentPage + 1);
                    if (chatHistory != null && chatHistory.Count > 0)
                    {
                        UpdateChatHistory(chatHistory, insertAtTop: true);
                        // 小延迟后修正滚动位置
                        await Task.Delay(50);
                        double newExtentHeight = scrollViewer.ExtentHeight;
                        double heightDiff = newExtentHeight - previousExtentHeight;
                        scrollViewer.ScrollToVerticalOffset(heightDiff);
                        _logger.LogDebug($"调整滚动位置，高度差: {heightDiff}");
                    }
                    else
                    {
                        _logger.LogDebug($"第 {_currentPage + 1} 页无更多聊天记录");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"获取第 {_currentPage + 1} 页聊天记录失败: {ex.Message}");
                }
                finally
                {
                    _isLoadingPage = false;
                }
            }
        }


        private string GetCachePath()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                if (!File.Exists(configPath))
                {
                    _logger.LogWarning("未找到 config.json");
                    return null;
                }

                var json = File.ReadAllText(configPath);
                var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);

                if (dict != null && dict.TryGetValue("cache_path", out var path))
                {
                    return path?.ToString();
                }
                else
                {
                    _logger.LogWarning("config.json 中缺少 cache_path 配置");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"读取 config.json 失败: {ex.Message}");
                return null;
            }
        }

        private string FindThumbnailPath(string fileId)
        {
            if (string.IsNullOrEmpty(fileId))
                return null;

            var cachePath = GetCachePath();
            if (string.IsNullOrEmpty(cachePath))
                return null;

            string thumbnailsDir = Path.Combine(cachePath, "thumbnails");
            string thumbnailPath = Path.Combine(thumbnailsDir, fileId);

            if (File.Exists(thumbnailPath))
            {
                _logger.LogDebug($"找到本地缩略图: {thumbnailPath}");
                return thumbnailPath;
            }
            else
            {
                _logger.LogWarning($"本地缩略图不存在: {thumbnailPath}");
                return null;
            }
        }


        private async Task<List<ChatHistoryEntry>> LoadChatHistoryPageAsync(string friendUsername, int page)
        {
            if (_link == null || string.IsNullOrEmpty(friendUsername))
            {
                _logger.LogWarning("无法加载聊天记录：Link 或好友用户名为空");
                return null;
            }

            try
            {
                _logger.LogDebug($"请求聊天记录，用户名: {friendUsername}，页码: {page}");
                var response = await _link.GetChatHistoryPaginated(friendUsername, page, PageSize);

                if (response.ContainsKey("errors"))
                {
                    _logger.LogError($"聊天记录解析包含错误: {JsonConvert.SerializeObject(response["errors"])}");
                    return null;
                }

                var chatHistory = (response.GetValueOrDefault("data") as List<Dictionary<string, object>>)
                    ?.Select(entry => new ChatHistoryEntry
                    {
                        Rowid = GetValueOrDefault<int>(entry, "rowid"),
                        WriteTime = GetValueOrDefault<string>(entry, "write_time"),
                        SenderUsername = GetValueOrDefault<string>(entry, "sender_username"),
                        Message = GetValueOrDefault<string>(entry, "message") ?? "",
                        IsCurrentUser = GetValueOrDefault<bool>(entry, "is_current_user"),
                        ReplyTo = GetValueOrDefault<int?>(entry, "reply_to"),
                        ReplyPreview = GetValueOrDefault<string>(entry, "reply_preview"),
                        AttachmentType = GetValueOrDefault<string>(entry, "attachment_type"),
                        FileId = GetValueOrDefault<string>(entry, "file_id"),
                        OriginalFileName = GetValueOrDefault<string>(entry, "original_file_name"),
                        ThumbnailPath = GetValueOrDefault<string>(entry, "thumbnail_path"),
                        ThumbnailLocalPath = GetValueOrDefault<string>(entry, "thumbnail_local_path"),
                        FileSize = GetValueOrDefault<long?>(entry, "file_size"),
                        Duration = GetValueOrDefault<double?>(entry, "duration")
                    })
                    .ToList();

                return chatHistory;
            }
            catch (Exception ex)
            {
                _logger.LogError($"加载聊天记录失败: {ex.Message}");
                return null;
            }
        }

        private static T GetValueOrDefault<T>(Dictionary<string, object> dict, string key)
        {
            if (dict.TryGetValue(key, out var value) && value != null)
            {
                try
                {
                    return (T)Convert.ChangeType(value, typeof(T));
                }
                catch (Exception ex)
                {
                    return default;
                }
            }
            return default;
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
                e.Handled = true;
            }
        }

        private T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T target)
                    return target;
                var result = FindVisualChild<T>(child);
                if (result != null)
                    return result;
            }
            return null;
        }
    }
}
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Client.Function
{
    public class Link : IDisposable
    {
        private readonly ILogger<Link> _logger;
        private readonly byte[] _encryptionKey;
        private readonly Dictionary<string, object> _config;
        private bool _isRunning;
        private TcpClient _clientSocket;
        private NetworkStream _stream;
        private readonly SemaphoreSlim _sendLock;
        private readonly Dictionary<string, TaskCompletionSource<Dictionary<string, object>>> _pendingRequests;
        private readonly Dictionary<string, TaskCompletionSource<Dictionary<string, object>>> _registerRequests;
        private readonly SemaphoreSlim _lock;
        private readonly string _cacheRoot;
        private readonly string _avatarDir;
        private bool _isAuthenticated;
        private Task _readerTask;
        private Task _registerTask;
        private bool _registerActive;
        private int? _replyToId;
        private bool _isClosing;


        public string CurrentFriend;
        public List<Dictionary<string, object>> Friends;
        public Dictionary<string, int> UnreadMessages;
        public string _username;
        public readonly string _thumbnailDir;

        // 事件（对应 Python 的 pyqtSignal）
        public event Action<List<Dictionary<string, object>>> FriendListUpdated;
        public event Action<List<Dictionary<string, object>>, List<string>, List<int>, bool> ConversationsUpdated;
        public event Action<Dictionary<string, object>> NewMessageReceived;
        public event Action<Dictionary<string, object>> NewMediaReceived;
        public event Action<Dictionary<string, object>> RemarksUpdated;

        public Link(ILoggerFactory loggerFactory, string host = "26.102.137.22", int port = 13235)
        {
            _logger = loggerFactory.CreateLogger<Link>();
            _encryptionKey = LoadEncryptionKey();
            _config = new Dictionary<string, object>
    {
        { "host", host },
        { "port", port },
        { "retries", 5 },
        { "delay", 2 }
    };
            _isRunning = true;
            _sendLock = new SemaphoreSlim(1, 1);
            _pendingRequests = new Dictionary<string, TaskCompletionSource<Dictionary<string, object>>>();
            _registerRequests = new Dictionary<string, TaskCompletionSource<Dictionary<string, object>>>();
            _lock = new SemaphoreSlim(1, 1);
            Friends = new List<Dictionary<string, object>>();
            UnreadMessages = new Dictionary<string, int>();
            CurrentFriend = null;

            // 从 config.json 加载缓存路径或创建默认 config.json
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            if (!File.Exists(configPath))
            {
                try
                {
                    // 创建默认配置
                    var defaultConfig = new Dictionary<string, string>
            {
                { "cache_path", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Chat_DATA") }
            };
                    string defaultConfigJson = JsonConvert.SerializeObject(defaultConfig, Formatting.Indented);
                    File.WriteAllText(configPath, defaultConfigJson);
                    _cacheRoot = defaultConfig["cache_path"];
                }
                catch (Exception ex)
                {
                    _cacheRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Chat_DATA");
                }
            }
            else
            {
                try
                {
                    string configJson = File.ReadAllText(configPath);
                    var config = JsonConvert.DeserializeObject<Dictionary<string, string>>(configJson);
                    _cacheRoot = config.ContainsKey("cache_path") ? config["cache_path"] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Chat_DATA");
                }
                catch (Exception ex)
                {
                    _cacheRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Chat_DATA");
                }
            }

            _avatarDir = Path.Combine(_cacheRoot, "avatars");
            _thumbnailDir = Path.Combine(_cacheRoot, "thumbnails");
            Directory.CreateDirectory(_avatarDir);
            Directory.CreateDirectory(_thumbnailDir);
        }

        private byte[] LoadEncryptionKey()
        {
            string key = Environment.GetEnvironmentVariable("ENCRYPTION_KEY");
            if (!string.IsNullOrEmpty(key))
            {
                return Convert.FromBase64String(key);
            }
            string keyFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "secret.key");
            try
            {
                var keyBytes = File.ReadAllBytes(keyFile);
                return keyBytes;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("无法加载加密密钥。", ex);
            }
        }

        private byte[] Encrypt(Dictionary<string, object> req)
        {
            string json = JsonConvert.SerializeObject(req, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            _logger.LogDebug($"加密前 JSON: {json}");
            using var aes = Aes.Create();
            aes.Key = _encryptionKey;
            aes.GenerateIV();
            aes.Mode = CipherMode.CBC;
            using var ms = new MemoryStream();
            ms.Write(aes.IV, 0, aes.IV.Length);
            using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
            {
                byte[] data = Encoding.UTF8.GetBytes(json);
                cs.Write(data, 0, data.Length);
                cs.FlushFinalBlock();
            }
            byte[] result = ms.ToArray();
            return result;
        }

        private Dictionary<string, object> Decrypt(byte[] cipherText)
        {
            using var aes = Aes.Create();
            aes.Key = _encryptionKey;
            byte[] iv = new byte[16];
            Array.Copy(cipherText, 0, iv, 0, 16);
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            using var ms = new MemoryStream(cipherText, 16, cipherText.Length - 16);
            using var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read);
            using var sr = new StreamReader(cs);
            string json = sr.ReadToEnd();
            _logger.LogDebug($"解密后 JSON: {json}");
            return JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
        }

        public async Task Start()
        {
            if (_registerActive && _registerTask != null)
            {
                _registerActive = false;
                _registerTask = null; // C# 不需要显式取消 Task，设置为 null
            }
            _readerTask = Task.Run(() => StartReader());
        }

        public async Task Connect()
        {
            if (_clientSocket != null && _clientSocket.Connected)
            {
                return;
            }

            // 清理旧连接
            if (_clientSocket != null)
            {
                try
                {
                    _clientSocket.Close();
                    _clientSocket.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"清理旧 socket 时出错: {ex.Message}");
                }
                _clientSocket = null;
                _stream = null;
            }

            int retries = Convert.ToInt32(_config["retries"]);
            int delay = Convert.ToInt32(_config["delay"]);
            for (int attempt = 0; attempt < retries; attempt++)
            {
                try
                {
                    _clientSocket = new TcpClient();
                    await _clientSocket.ConnectAsync(_config["host"].ToString(), Convert.ToInt32(_config["port"]));
                    _stream = _clientSocket.GetStream();
                    _isRunning = true;
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"连接尝试 {attempt + 1}/{retries} 失败: {ex.Message}");
                    if (_clientSocket != null)
                    {
                        try
                        {
                            _clientSocket.Close();
                            _clientSocket.Dispose();
                        }
                        catch (Exception closeEx)
                        {
                            _logger.LogError($"关闭失败的 socket 时出错: {closeEx.Message}");
                        }
                        _clientSocket = null;
                        _stream = null;
                    }
                    if (attempt < retries - 1)
                    {
                        await Task.Delay(delay * 1000);
                    }
                }
            }
            throw new Exception("无法连接到服务器");
        }

        private byte[] PackMessage(byte[] data)
        {
            byte[] lengthBytes = BitConverter.GetBytes(data.Length);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(lengthBytes);
            }
            byte[] result = new byte[4 + data.Length];
            Array.Copy(lengthBytes, 0, result, 0, 4);
            Array.Copy(data, 0, result, 4, data.Length);
            return result;
        }

        private Dictionary<string, object> SyncSendRecv(Dictionary<string, object> req)
        {
            try
            {
                byte[] ciphertext = Encrypt(req);
                byte[] msg = PackMessage(ciphertext);
                _stream.Write(msg, 0, msg.Length);
                byte[] header = RecvAll(4);
                if (header == null || header.Length < 4)
                {
                    throw new Exception("响应头不完整");
                }
                int length = BitConverter.ToInt32(header.Reverse().ToArray(), 0);
                byte[] encryptedResponse = RecvAll(length);
                return Decrypt(encryptedResponse);
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "status", "error" }, { "message", ex.Message } };
            }
        }

        private byte[] RecvAll(int length)
        {
            byte[] data = new byte[length];
            int received = 0;
            while (received < length)
            {
                int chunkSize = _stream.Read(data, received, length - received);
                if (chunkSize == 0)
                {
                    throw new Exception("接收数据异常");
                }
                received += chunkSize;
            }
            return data;
        }

        private async Task<byte[]> RecvAsync(int size)
        {
            byte[] data = new byte[size];
            int received = 0;
            while (received < size)
            {
                int chunkSize = await _stream.ReadAsync(data, received, size - received);
                if (chunkSize == 0)
                {
                    throw new Exception("连接断开");
                }
                received += chunkSize;
            }
            return data;
        }

        public async Task<string> Authenticate(string username, string password)
        {
            var req = new Dictionary<string, object>
            {
                { "type", "authenticate" },
                { "username", username },
                { "password", password },
                { "request_id", Guid.NewGuid().ToString() }
            };
            if (_clientSocket == null)
            {
                return "连接未建立";
            }
            try
            {
                var resp = await Task.Run(() => SyncSendRecv(req));
                if (resp.GetValueOrDefault("status")?.ToString() == "success")
                {
                    _username = username;
                    _isAuthenticated = true;
                    return "认证成功";
                }
                return resp.GetValueOrDefault("message")?.ToString() ?? "账号或密码错误";
            }
            catch (Exception ex)
            {
                if (_clientSocket != null)
                {
                    try
                    {
                        _clientSocket.Close();
                    }
                    catch (Exception closeEx)
                    {
                        _logger.LogError($"关闭 socket 时出错: {closeEx.Message}");
                    }
                    _clientSocket = null;
                }
                return $"认证失败: {ex.Message}";
            }
        }

        public async Task<Dictionary<string, object>> UpdateFriendRemarks(string friend, string remarks)
        {
            var req = new Dictionary<string, object>
            {
                { "type", "Update_Remarks" },
                { "username", _username },
                { "friend", friend },
                { "remarks", remarks },
                { "request_id", Guid.NewGuid().ToString() }
            };
            var resp = await SendRequest(req);
            if (resp.GetValueOrDefault("status")?.ToString() == "success")
            {
                foreach (var f in Friends)
                {
                    if (f.GetValueOrDefault("username")?.ToString() == friend)
                    {
                        f["name"] = resp.GetValueOrDefault("remarks") ?? remarks;
                        break;
                    }
                }
                FriendListUpdated?.Invoke(Friends);
            }
            return resp;
        }

        private async Task StartReader()
        {
            while (_isRunning)
            {
                try
                {
                    if (_clientSocket == null || !_clientSocket.Connected || _stream == null)
                    {
                        break;
                    }
                    byte[] header = await RecvAsync(4);
                    if (header.Length < 4)
                    {
                        continue;
                    }
                    int length = BitConverter.ToInt32(header.Reverse().ToArray(), 0);
                    byte[] encryptedPayload = await RecvAsync(length);
                    var resp = Decrypt(encryptedPayload);
                    if (resp == null)
                    {
                        continue;
                    }

                    string respJson = JsonConvert.SerializeObject(resp, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                    _logger.LogDebug($"收到推送消息: {respJson}");

                    if (!resp.ContainsKey("type") || resp["type"] == null)
                    {
                        _logger.LogWarning($"消息缺少 type 键或 type 为 null: {respJson}");
                        continue;
                    }

                    string responseType = resp["type"].ToString();
                    if (!_isAuthenticated && responseType != "user_register" && responseType != "exit")
                    {
                        continue;
                    }

                    if (responseType == "exit")
                    {
                        _isRunning = false;
                        string reqId = resp.GetValueOrDefault("request_id")?.ToString();
                        if (!string.IsNullOrEmpty(reqId) && _pendingRequests.ContainsKey(reqId))
                        {
                            _pendingRequests[reqId].SetResult(resp);
                            _pendingRequests.Remove(reqId);
                        }
                        break;
                    }

                    if (responseType == "friend_list_update" || responseType == "friend_update")
                    {
                        await HandleFriendUpdates(resp);
                        continue;
                    }
                    else if (responseType == "new_message" || responseType == "new_media")
                    {
                        await ParsingNewMessageOrMedia(resp);
                        continue;
                    }
                    else if (responseType == "deleted_messages")
                    {
                        await ParsingDeleteMessage(resp);
                        continue;
                    }
                    else if (responseType == "Update_Remarks")
                    {
                        RemarksUpdated?.Invoke(resp);
                        continue;
                    }

                    string requestId = resp.GetValueOrDefault("request_id")?.ToString();
                    if (!string.IsNullOrEmpty(requestId) && _pendingRequests.ContainsKey(requestId))
                    {
                        _pendingRequests[requestId].SetResult(resp);
                        _pendingRequests.Remove(requestId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"读取推送消息失败: {ex.Message}\nStackTrace: {ex.StackTrace}");
                    if (!_isRunning)
                    {
                        break;
                    }
                    await Task.Delay(1000);
                }
            }
        }

        private async Task HandleFriendUpdates(Dictionary<string, object> resp)
        {
            string responseType = resp["type"].ToString();
            if (responseType == "friend_list_update")
            {
                var friendsObj = resp.GetValueOrDefault("friends");
                if (friendsObj == null)
                {
                    Friends = new List<Dictionary<string, object>>();
                }
                else
                {
                    try
                    {
                        if (friendsObj is JArray jArray)
                        {
                            Friends = jArray.Select(item => ConvertJObjectToDictionary(item)).ToList();
                        }
                        else if (friendsObj is List<object> list)
                        {
                            Friends = list.Select(item => ConvertJObjectToDictionary(item)).ToList();
                        }
                        else
                        {
                            _logger.LogWarning($"friend_list_update 的 friends 字段类型未知: {friendsObj.GetType()}");
                            Friends = new List<Dictionary<string, object>>();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"解析 friend_list_update 的 friends 字段失败: {ex.Message}\nStackTrace: {ex.StackTrace}");
                        Friends = new List<Dictionary<string, object>>();
                    }
                }
                _logger.LogDebug($"触发 FriendListUpdated，好友数量: {Friends.Count}");
                FriendListUpdated?.Invoke(Friends);
            }
            else if (responseType == "friend_update")
            {
                var friend = resp.GetValueOrDefault("friend");
                if (friend != null)
                {
                    var convertedFriend = ConvertJObjectToDictionary(friend);
                    string friendId = convertedFriend.GetValueOrDefault("username")?.ToString();
                    if (!string.IsNullOrEmpty(friendId))
                    {
                        int index = Friends.FindIndex(f => f.GetValueOrDefault("username")?.ToString() == friendId);
                        if (index >= 0)
                        {
                            Friends[index] = convertedFriend;
                        }
                        else
                        {
                            Friends.Add(convertedFriend);
                        }
                        _logger.LogDebug($"触发 FriendListUpdated，更新好友: {friendId}");
                        FriendListUpdated?.Invoke(Friends);
                    }
                }
            }
        }

        private Dictionary<string, object> ConvertJObjectToDictionary(object item)
        {
            Dictionary<string, object> result;
            if (item is JObject jObject)
            {
                result = jObject.ToObject<Dictionary<string, object>>();
            }
            else if (item is Dictionary<string, object> dict)
            {
                result = new Dictionary<string, object>(dict);
            }
            else
            {
                return new Dictionary<string, object>();
            }

            if (result.ContainsKey("conversations") && result["conversations"] != null)
            {
                if (result["conversations"] is JObject convJObject)
                {
                    result["conversations"] = convJObject.ToObject<Dictionary<string, object>>();
                }
                else if (result["conversations"] is Dictionary<string, object>)
                {
                    // 已经是 Dictionary，无需转换
                }
                else
                {
                    result["conversations"] = new Dictionary<string, object>();
                }
            }

            return result;
        }

        private async Task RegisterReader()
        {
            _registerActive = true;
            try
            {
                while (_registerActive && _clientSocket != null && _clientSocket.Connected)
                {
                    byte[] header = await RecvAsync(4);
                    if (header.Length < 4)
                    {
                        continue;
                    }
                    int length = BitConverter.ToInt32(header.Reverse().ToArray(), 0);
                    byte[] encryptedPayload = await RecvAsync(length);
                    var resp = Decrypt(encryptedPayload);

                    string responseType = resp.GetValueOrDefault("type")?.ToString();
                    string reqId = resp.GetValueOrDefault("request_id")?.ToString();

                    if (responseType == "user_register" && _registerRequests.ContainsKey(reqId))
                    {
                        _registerRequests[reqId].SetResult(resp);
                        _registerRequests.Remove(reqId);
                    }
                    else if (responseType == "exit")
                    {
                        _logger.LogInformation("RegisterReader 收到退出响应，结束注册读取");
                        if (_registerRequests.ContainsKey(reqId))
                        {
                            _registerRequests[reqId].SetResult(resp);
                            _registerRequests.Remove(reqId);
                        }
                        _registerActive = false;
                        break;
                    }
                    else
                    {
                        _logger.LogWarning($"意外的响应类型: {responseType}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"RegisterReader 失败: {ex.Message}\nStackTrace: {ex.StackTrace}");
                foreach (var fut in _registerRequests.Values.ToList())
                {
                    if (!fut.Task.IsCompleted)
                    {
                        fut.SetException(ex);
                    }
                }
            }
            finally
            {
                _registerActive = false;
                _registerTask = null;
                _logger.LogInformation("RegisterReader 已退出");
            }
        }

        public async Task<Dictionary<string, object>> Register(string subtype, string sessionId = null, string captchaInput = null,
    string password = null, Image avatar = null, string nickname = null, string sign = null)
        {
            var req = new Dictionary<string, object>
    {
        { "type", "user_register" },
        { "subtype", subtype },
        { "request_id", Guid.NewGuid().ToString() }
    };
            if (!string.IsNullOrEmpty(sessionId))
            {
                req["session_id"] = sessionId;
            }

            // 确保连接可用
            try
            {
                await Connect();
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "status", "error" }, { "message", "无法连接到服务器，请检查网络后重试" } };
            }

            if (!_registerActive)
            {
                _registerTask = Task.Run(() => RegisterReader());
            }

            if (subtype == "register_1" || subtype == "register_4")
            {
                var resp = await SendAndWaitForResponse(req);
                if (resp.GetValueOrDefault("status")?.ToString() == "success")
                {
                    return new Dictionary<string, object>
            {
                { "status", "success" },
                { "username", resp.GetValueOrDefault("username") },
                { "captcha_image", resp.GetValueOrDefault("captcha_image") },
                { "session_id", resp.GetValueOrDefault("session_id") }
            };
                }
                return resp;
            }
            else if (subtype == "register_2")
            {
                if (string.IsNullOrEmpty(captchaInput))
                {
                    return new Dictionary<string, object> { { "status", "error" }, { "message", "请输入验证码" } };
                }
                req["captcha_input"] = captchaInput;
                return await SendAndWaitForResponse(req);
            }
            else if (subtype == "register_3")
            {
                if (string.IsNullOrEmpty(password))
                {
                    return new Dictionary<string, object> { { "status", "error" }, { "message", "密码不能为空" } };
                }
                req["password"] = password;
                if (avatar != null)
                {
                    using var ms = new MemoryStream();
                    avatar.Save(ms, ImageFormat.Jpeg);
                    byte[] fileData = ms.ToArray();
                    req["avatar_data"] = Convert.ToBase64String(fileData);
                }
                req["nickname"] = nickname ?? "";
                req["sign"] = sign ?? "";
                var resp = await SendAndWaitForResponse(req);
                if (resp.GetValueOrDefault("status")?.ToString() == "success")
                {
                    _registerActive = false;
                    _registerTask = null;
                }
                return resp;
            }
            return new Dictionary<string, object> { { "status", "error" }, { "message", "未知的子类型" } };
        }

        private async Task<Dictionary<string, object>> SendAndWaitForResponse(Dictionary<string, object> req)
        {
            try
            {
                await _sendLock.WaitAsync();
                try
                {
                    byte[] ciphertext = Encrypt(req);
                    byte[] msg = PackMessage(ciphertext);
                    _logger.LogDebug($"发送注册请求: subtype={req["subtype"]}, request_id={req["request_id"]}");
                    await _stream.WriteAsync(msg, 0, msg.Length);
                }
                finally
                {
                    _sendLock.Release();
                }

                var fut = new TaskCompletionSource<Dictionary<string, object>>();
                _registerRequests[req["request_id"].ToString()] = fut;
                var resp = await fut.Task;
                _logger.LogDebug($"收到注册响应: subtype={req["subtype"]}, response={JsonConvert.SerializeObject(resp)}");
                return resp;
            }
            catch (Exception ex)
            {
                _logger.LogError($"发送注册请求失败: subtype={req["subtype"]}, error={ex.Message}\nStackTrace: {ex.StackTrace}");
                return new Dictionary<string, object> { { "status", "error" }, { "message", $"发送请求失败: {ex.Message}" } };
            }
        }

        private async Task ParsingNewMessageOrMedia(Dictionary<string, object> resp)
        {
            string sender = resp.GetValueOrDefault("from")?.ToString();
            if (string.IsNullOrEmpty(sender))
            {
                _logger.LogWarning("新消息缺少发送者，忽略");
                return;
            }

            foreach (var friend in Friends)
            {
                if (friend.GetValueOrDefault("username")?.ToString() == sender)
                {
                    var newConversation = new Dictionary<string, object>
            {
                { "sender", resp.GetValueOrDefault("from") },
                { "content", resp.GetValueOrDefault("type")?.ToString() == "new_media"
                    ? $"[{resp.GetValueOrDefault("file_type") ?? "文件"}]"
                    : resp.GetValueOrDefault("message") ?? "" },
                { "last_update_time", resp.GetValueOrDefault("write_time")?.ToString() ?? "" }
            };
                    friend["conversations"] = newConversation;
                    _logger.LogDebug($"更新好友会话，Username: {sender}, Content: {newConversation["content"]}, LastUpdateTime: {newConversation["last_update_time"]}");
                    break;
                }
            }

            bool shouldIncrementUnread = sender != CurrentFriend;
            if (shouldIncrementUnread)
            {
                UnreadMessages[sender] = UnreadMessages.GetValueOrDefault(sender, 0) + 1;
            }

            FriendListUpdated?.Invoke(Friends); // 触发更新
            if (resp.GetValueOrDefault("type")?.ToString() == "new_message")
            {
                NewMessageReceived?.Invoke(resp);
            }
            else if (resp.GetValueOrDefault("type")?.ToString() == "new_media")
            {
                NewMediaReceived?.Invoke(resp);
            }
        }

        public void ClearUnreadMessages(string friend)
        {
            if (UnreadMessages.ContainsKey(friend))
            {
                UnreadMessages[friend] = 0;
            }
        }

        public async Task<Dictionary<string, object>> GetUserInfo()
        {
            var req = new Dictionary<string, object>
    {
        { "type", "get_user_info" },
        { "username", _username },
        { "request_id", Guid.NewGuid().ToString() }
    };
            var resp = await SendRequest(req);
            if (resp.GetValueOrDefault("status")?.ToString() == "success" && resp.ContainsKey("avatar_id"))
            {
                string avatarId = resp["avatar_id"].ToString();
                string savePath = Path.Combine(_avatarDir, avatarId);
                if (!File.Exists(savePath))
                {
                    var results = await DownloadMedia(
                        new List<(string, string)> { (avatarId, savePath) },
                        "avatar",
                        null
                    );
                    if (results.FirstOrDefault()?.GetValueOrDefault("status")?.ToString() != "success")
                    {
                        _logger.LogError($"头像下载失败: file_id={avatarId}, message={results.FirstOrDefault()?.GetValueOrDefault("message")}");
                    }
                }
                resp["avatar_local_path"] = savePath;
            }
            return resp;
        }

        private async Task<Dictionary<string, object>> SendFileChunks(Dictionary<string, object> req, string filePath,
            Action<string, double, string> progressCallback = null, int chunkSize = 1024 * 1024)
        {
            long fileSize = new FileInfo(filePath).Length;
            long totalSent = 0;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                byte[] buffer = new byte[chunkSize];
                int bytesRead;
                while ((bytesRead = await fs.ReadAsync(buffer, 0, chunkSize)) > 0)
                {
                    byte[] chunk = new byte[bytesRead];
                    Array.Copy(buffer, chunk, bytesRead);
                    string chunkB64 = Convert.ToBase64String(chunk);
                    var chunkReq = new Dictionary<string, object>(req)
                    {
                        { "file_data", chunkB64 },
                        { "total_size", fileSize },
                        { "sent_size", totalSent + chunk.Length }
                    };
                    await SendRequest(chunkReq);
                    totalSent += chunk.Length;
                    if (progressCallback != null)
                    {
                        double progress = (double)totalSent / fileSize * 100;
                        progressCallback("upload", progress, Path.GetFileName(filePath));
                    }
                }
            }
            return new Dictionary<string, object> { { "status", "success" } };
        }

        public async Task<Dictionary<string, object>> SendRequest(Dictionary<string, object> req)
        {
            if (_clientSocket == null || !_clientSocket.Connected || _stream == null)
            {
                _logger.LogWarning("无法发送请求：连接不可用。");
                return new Dictionary<string, object> { { "status", "error" }, { "message", "连接不可用" } };
            }

            try
            {
                await _sendLock.WaitAsync();
                try
                {
                    byte[] ciphertext = Encrypt(req);
                    byte[] msg = PackMessage(ciphertext);
                    await _stream.WriteAsync(msg, 0, msg.Length);
                }
                finally
                {
                    _sendLock.Release();
                }
                var fut = new TaskCompletionSource<Dictionary<string, object>>();
                _pendingRequests[req["request_id"].ToString()] = fut;
                return await fut.Task;
            }
            catch (Exception ex)
            {
                _logger.LogError($"发送请求失败: {ex.Message}");
                return new Dictionary<string, object> { { "status", "error" }, { "message", ex.Message } };
            }
        }

        public async Task<Dictionary<string, object>> GetChatHistoryPaginated(string friend, int page, int pageSize)
        {
            var req = new Dictionary<string, object>
    {
        { "type", "get_chat_history_paginated" },
        { "username", _username },
        { "friend", friend },
        { "page", page },
        { "page_size", pageSize },
        { "request_id", Guid.NewGuid().ToString() }
    };

            var resp = await SendRequest(req);
            var parsedResp = await ParseResponse(resp);

            _logger.LogDebug($"GetChatHistoryPaginated: 解析后响应: {JsonConvert.SerializeObject(parsedResp)}");

            var data = parsedResp["data"] as List<Dictionary<string, object>>;
            if (data == null)
            {
                _logger.LogError("parsedResp['data'] 类型错误，非 List<Dictionary<string, object>>");
                return new Dictionary<string, object>
        {
            { "status", "error" },
            { "message", "聊天记录数据格式错误" },
            { "request_id", parsedResp.GetValueOrDefault("request_id") }
        };
            }
            var thumbnailsToDownload = new List<(string fileId, string savePath)>();
            foreach (var entry in data)
            {
                if (entry.TryGetValue("file_id", out var fidObj) &&
                    new[] { "image", "video" }.Contains(entry.GetValueOrDefault("attachment_type")?.ToString()))
                {
                    var fileId = fidObj?.ToString();
                    if (string.IsNullOrEmpty(fileId))
                    {
                        _logger.LogWarning($"file_id 为空，跳过缩略图下载: {JsonConvert.SerializeObject(entry)}");
                        continue;
                    }

                    var savePath = Path.Combine(_thumbnailDir, fileId);
                    entry["thumbnail_local_path"] = savePath;

                    if (!File.Exists(savePath) || new FileInfo(savePath).Length == 0)
                    {
                        thumbnailsToDownload.Add((fileId, savePath));
                    }
                }
            }
            if (thumbnailsToDownload.Any())
            {
                _logger.LogDebug($"后台启动批量下载缩略图，共 {thumbnailsToDownload.Count} 个");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var results = await DownloadMedia(thumbnailsToDownload, "thumbnail");
                        foreach (var result in results)
                        {
                            if (result.GetValueOrDefault("status")?.ToString() != "success")
                            {
                                var badId = result.GetValueOrDefault("file_id")?.ToString();
                                var reason = result.GetValueOrDefault("reason")?.ToString() ?? result.GetValueOrDefault("message");
                                _logger.LogError($"缩略图下载失败: file_id={badId}, reason={reason}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"后台缩略图下载任务异常: {ex.Message}");
                    }
                });
            }
            return parsedResp;
        }

        public async Task<Dictionary<string, object>> SendMessage(string toUser, string message, int? replyTo = null)
        {
            var req = new Dictionary<string, object>
            {
                { "type", "send_message" },
                { "from", _username },
                { "to", toUser },
                { "message", message },
                { "request_id", Guid.NewGuid().ToString() }
            };
            if (replyTo.HasValue)
            {
                req["reply_to"] = replyTo.Value;
            }
            var resp = await SendRequest(req);
            if (resp.GetValueOrDefault("status")?.ToString() == "success")
            {
                foreach (var friend in Friends)
                {
                    if (friend.GetValueOrDefault("username")?.ToString() == toUser)
                    {
                        friend["conversations"] = new Dictionary<string, object>
                        {
                            { "sender", _username },
                            { "content", resp.GetValueOrDefault("conversations") ?? message },
                            { "last_update_time", resp.GetValueOrDefault("write_time") ?? "" }
                        };
                        break;
                    }
                }
                ConversationsUpdated?.Invoke(Friends, new List<string> { toUser }, new List<int>(), false);
            }
            return resp;
        }

        public async Task<Dictionary<string, object>> SendMedia(string toUser, string filePath, string fileType, int? replyTo = null,
            string message = "", Action<string, double, string> progressCallback = null)
        {
            try
            {
                string originalFileName = Path.GetFileName(filePath);
                string requestId = Guid.NewGuid().ToString();
                var req = new Dictionary<string, object>
                {
                    { "type", "send_media" },
                    { "from", _username },
                    { "to", toUser },
                    { "file_name", originalFileName },
                    { "file_type", fileType },
                    { "request_id", requestId },
                    { "message", message }
                };
                if (replyTo.HasValue)
                {
                    req["reply_to"] = replyTo.Value;
                }

                await SendFileChunks(req, filePath, progressCallback);
                var finalReq = new Dictionary<string, object>(req) { { "file_data", "" } };
                var response = await SendRequest(finalReq);
                if (response.GetValueOrDefault("status")?.ToString() == "success")
                {
                    if (response.ContainsKey("text_message"))
                    {
                        response["message"] = response["text_message"];
                    }
                    foreach (var friend in Friends)
                    {
                        if (friend.GetValueOrDefault("username")?.ToString() == toUser)
                        {
                            friend["conversations"] = new Dictionary<string, object>
                            {
                                { "sender", _username },
                                { "content", response.GetValueOrDefault("conversations") ?? $"[{fileType}]" },
                                { "last_update_time", response.GetValueOrDefault("write_time") ?? "" }
                            };
                            break;
                        }
                    }
                    ConversationsUpdated?.Invoke(Friends, new List<string> { toUser }, new List<int>(), false);
                }
                return response;
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "status", "error" }, { "message", ex.Message } };
            }
        }

        public async Task<List<Dictionary<string, object>>> SendMultipleMedia(string toUser, List<string> filePaths,
            Action<string, double, string> progressCallback = null, string message = "", int? replyTo = null)
        {
            var tasks = filePaths.Select(filePath =>
            {
                string fileType = DetectFileType(filePath);
                return SendMedia(toUser, filePath, fileType, replyTo, message, progressCallback);
            }).ToList();
            var results = await Task.WhenAll(tasks);
            return results.ToList();
        }

        private string DetectFileType(string filePath)
        {
            var imageExtensions = new HashSet<string> { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tiff", ".ico" };
            var videoExtensions = new HashSet<string> { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm" };
            string ext = Path.GetExtension(filePath).ToLower();
            if (imageExtensions.Contains(ext))
            {
                return "image";
            }
            else if (videoExtensions.Contains(ext))
            {
                return "video";
            }
            return "file";
        }

        private async Task ParsingDeleteMessage(Dictionary<string, object> resp)
        {
            string user1, user2;
            if (resp.GetValueOrDefault("type")?.ToString() == "messages_deleted")
            {
                user1 = _username;
                user2 = CurrentFriend;
            }
            else
            {
                user2 = resp.GetValueOrDefault("from")?.ToString();
                user1 = resp.GetValueOrDefault("to")?.ToString();
            }
            string conversations = resp.GetValueOrDefault("conversations")?.ToString() ?? "";
            string writeTime = resp.GetValueOrDefault("write_time")?.ToString() ?? "";
            bool showFloatingLabel = Convert.ToBoolean(resp.GetValueOrDefault("show_floating_label") ?? false);

            Dictionary<string, object> newConversation = null;
            if (!string.IsNullOrEmpty(conversations))
            {
                newConversation = new Dictionary<string, object>
                {
                    { "sender", user1 },
                    { "content", conversations },
                    { "last_update_time", writeTime }
                };
            }
            foreach (var friend in Friends)
            {
                if (new[] { user1, user2 }.Contains(friend.GetValueOrDefault("username")?.ToString()))
                {
                    friend["conversations"] = newConversation;
                }
            }

            var deletedRowids = (resp.GetValueOrDefault("deleted_rowids") as List<object>)?.Select(x => Convert.ToInt32(x)).ToList() ?? new List<int>();
            var affectedUsers = string.IsNullOrEmpty(user2) ? new List<string>() : new List<string> { user2 };
            ConversationsUpdated?.Invoke(Friends, affectedUsers, deletedRowids, showFloatingLabel);
        }

        public async Task<Dictionary<string, object>> DeleteMessages(object rowids)
        {
            if (!_isAuthenticated)
            {
                return new Dictionary<string, object> { { "status", "error" }, { "message", "未登录，无法删除消息" } };
            }
            var req = new Dictionary<string, object>
            {
                { "type", "delete_messages" },
                { "username", _username },
                { "request_id", Guid.NewGuid().ToString() }
            };
            if (rowids is int rowid)
            {
                req["rowid"] = rowid;
            }
            else if (rowids is List<int> rowidList)
            {
                if (!rowidList.Any())
                {
                    return new Dictionary<string, object> { { "status", "error" }, { "message", "消息ID列表不能为空" } };
                }
                req["rowids"] = rowidList;
            }
            else
            {
                return new Dictionary<string, object> { { "status", "error" }, { "message", "rowids 参数必须是整数或整数列表" } };
            }

            var resp = await SendRequest(req);
            await ParsingDeleteMessage(resp);
            return resp;
        }

        public async Task<Dictionary<string, object>> UploadAvatar(Image avatar)
        {
            using var ms = new MemoryStream();
            avatar.Save(ms, ImageFormat.Jpeg);
            byte[] fileData = ms.ToArray();
            string fileDataB64 = Convert.ToBase64String(fileData);

            var req = new Dictionary<string, object>
    {
        { "type", "upload_avatar" },
        { "username", _username },
        { "file_data", fileDataB64 },
        { "request_id", Guid.NewGuid().ToString() }
    };
            var resp = await SendRequest(req);
            if (resp.GetValueOrDefault("status")?.ToString() == "success" && resp.ContainsKey("avatar_id"))
            {
                string avatarId = resp["avatar_id"].ToString();
                string savePath = Path.Combine(_avatarDir, avatarId);
                if (!File.Exists(savePath))
                {
                    var results = await DownloadMedia(
                        new List<(string, string)> { (avatarId, savePath) },
                        "avatar",
                        null
                    );
                    if (results.FirstOrDefault()?.GetValueOrDefault("status")?.ToString() != "success")
                    {
                        _logger.LogError($"头像下载失败: file_id={avatarId}, message={results.FirstOrDefault()?.GetValueOrDefault("message")}");
                    }
                }
                resp["avatar_local_path"] = savePath;
            }
            return resp;
        }

        public async Task<Dictionary<string, object>> UpdateName(string newName)
        {
            if (!_isAuthenticated)
            {
                return new Dictionary<string, object> { { "status", "error" }, { "message", "未登录，无法更改昵称" } };
            }
            var req = new Dictionary<string, object>
            {
                { "type", "update_name" },
                { "username", _username },
                { "new_name", newName },
                { "request_id", Guid.NewGuid().ToString() }
            };
            return await SendRequest(req);
        }

        public async Task<Dictionary<string, object>> UpdateSign(string newSign)
        {
            if (!_isAuthenticated)
            {
                return new Dictionary<string, object> { { "status", "error" }, { "message", "未登录，无法更改签名" } };
            }
            var req = new Dictionary<string, object>
            {
                { "type", "update_sign" },
                { "username", _username },
                { "sign", newSign },
                { "request_id", Guid.NewGuid().ToString() }
            };
            return await SendRequest(req);
        }

        /// <summary>
        /// 简化后的客户端下载方法，保留原始签名及辅助函数，实现初始化、单次与分块下载。
        /// </summary>
        public async Task<List<Dictionary<string, object>>> DownloadMedia(
    List<(string fileId, string savePath)> fileRequests,
    string downloadType = "default",
    Action<string, double, string> progressCallback = null)
        {
            var results = new List<Dictionary<string, object>>();
            if (fileRequests == null || fileRequests.Count == 0 || _clientSocket?.Connected != true || _stream == null)
            {
                return results;
            }

            // —— Step A: 初始化阶段 ——
            var initReq = new Dictionary<string, object>
            {
                ["type"] = "download_media",
                ["file_ids"] = fileRequests.Select(r => r.fileId).ToList(),
                ["download_type"] = downloadType,
                ["request_id"] = Guid.NewGuid().ToString()
            };

            var initResp = await SendRequestAsync(initReq);
            if (initResp.GetValueOrDefault("status")?.ToString() != "success")
            {
                _logger.LogError($"下载初始化失败: {initResp.GetValueOrDefault("message")} ");
                return results;
            }

            // —— Step B: 防御式读取 file_sizes 和 file_checksums ——
            if (!initResp.TryGetValue("file_sizes", out var sizesToken) ||
                !initResp.TryGetValue("file_checksums", out var checksToken))
            {
                _logger.LogError("初始化响应缺少 file_sizes 或 file_checksums");
                return results;
            }

            var fileSizes = JsonConvert.DeserializeObject<Dictionary<string, long>>(sizesToken.ToString());
            var fileChecksums = JsonConvert.DeserializeObject<Dictionary<string, string>>(checksToken.ToString());

            // 遍历每个文件进行下载
            foreach (var (fileId, savePath) in fileRequests)
            {
                string tempPath = savePath + ".tmp";
                Directory.CreateDirectory(Path.GetDirectoryName(savePath));

                bool isSuccess = false;
                string failReason = null;

                try
                {
                    long expectedSize = fileSizes.TryGetValue(fileId, out var sz) ? sz : 0;
                    // 根据文件大小决定：<= 1MB 使用一次性下载，否则分块下载
                    bool useSingleRequest = expectedSize > 0 && expectedSize <= 1024 * 1024;

                    _logger.LogDebug($"准备下载文件: {fileId}, useSingleRequest={useSingleRequest}, 目标路径: {savePath}");

                    if (useSingleRequest)
                    {
                        isSuccess = await DownloadSingleAsync(fileId, tempPath, downloadType);
                    }
                    else
                    {
                        isSuccess = await DownloadChunksAsync(fileId, tempPath, downloadType, progressCallback);
                    }

                    // 验证下载结果
                    if (isSuccess)
                    {
                        bool valid = ValidateFile(tempPath, expectedSize, fileChecksums, fileId);
                        if (!valid)
                        {
                            isSuccess = false;
                            failReason = "文件校验失败";
                        }
                    }

                    // 成功则移动到最终路径，否则记录失败
                    if (isSuccess)
                    {
                        File.Move(tempPath, savePath, true);
                        _logger.LogInformation($"文件下载成功: {fileId} -> {savePath}");
                        results.Add(new Dictionary<string, object>
                        {
                            ["status"] = "success",
                            ["file_id"] = fileId,
                            ["save_path"] = savePath
                        });
                    }
                    else
                    {
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                        _logger.LogWarning($"文件下载失败: {fileId}, 原因: {failReason}");
                        results.Add(new Dictionary<string, object>
                        {
                            ["status"] = "error",
                            ["file_id"] = fileId,
                            ["reason"] = failReason ?? "下载过程失败"
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"下载文件 {fileId} 异常: {ex.Message}");
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    results.Add(new Dictionary<string, object>
                    {
                        ["status"] = "error",
                        ["file_id"] = fileId,
                        ["reason"] = ex.Message
                    });
                }
            }

            return results;
        }

        // 通用发送请求并等待响应
        private async Task<Dictionary<string, object>> SendRequestAsync(Dictionary<string, object> payload)
        {
            string requestId = payload["request_id"].ToString();
            var tcs = new TaskCompletionSource<Dictionary<string, object>>();
            _pendingRequests[requestId] = tcs;

            var data = PackMessage(Encrypt(payload));

            await _sendLock.WaitAsync();
            try
            {
                await _stream.WriteAsync(data, 0, data.Length);
                _logger.LogDebug($"请求发送成功，RequestId={requestId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"发送请求失败: {ex.Message}");
                _pendingRequests.Remove(requestId);
                return new Dictionary<string, object> { ["status"] = "error", ["message"] = "发送失败" };
            }
            finally
            {
                _sendLock.Release();
            }

            var timeoutTask = Task.Delay(30000); // 30秒超时
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            _pendingRequests.Remove(requestId);

            if (completedTask == tcs.Task)
            {
                return await tcs.Task;
            }
            else
            {
                _logger.LogError($"请求超时: RequestId={requestId}");
                return new Dictionary<string, object> { ["status"] = "error", ["message"] = "请求超时" };
            }
        }


        // 一次性下载小文件
        private async Task<bool> DownloadSingleAsync(string fileId, string tempPath, string downloadType)
        {
            try
            {
                var req = new Dictionary<string, object>
                {
                    ["type"] = "download_media",
                    ["file_id"] = fileId,
                    ["download_type"] = downloadType,
                    ["request_id"] = Guid.NewGuid().ToString(),
                    ["single_request"] = true
                };

                var resp = await SendRequestAsync(req);

                if (resp.GetValueOrDefault("status")?.ToString() != "success")
                {
                    _logger.LogError($"单请求下载失败: {resp.GetValueOrDefault("message")}");
                    return false;
                }

                var base64Data = resp.GetValueOrDefault("file_data")?.ToString();
                if (string.IsNullOrEmpty(base64Data))
                {
                    _logger.LogError("单请求返回数据为空");
                    return false;
                }

                await File.WriteAllBytesAsync(tempPath, Convert.FromBase64String(base64Data));
                _logger.LogDebug($"单请求下载完成: {fileId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"单请求下载异常: {ex.Message}");
                return false;
            }
        }


        // 分块下载大文件
        private async Task<bool> DownloadChunksAsync(
        string fileId,
        string tempPath,
        string downloadType,
        Action<string, double, string> progressCallback)
        {
            long offset = 0;

            try
            {
                while (true)
                {
                    var req = new Dictionary<string, object>
                    {
                        ["type"] = "download_media",
                        ["file_id"] = fileId,
                        ["offset"] = offset,
                        ["download_type"] = downloadType,
                        ["request_id"] = Guid.NewGuid().ToString()
                    };

                    var resp = await SendRequestAsync(req);

                    if (resp.GetValueOrDefault("status")?.ToString() != "success")
                    {
                        _logger.LogError($"分块下载失败: {resp.GetValueOrDefault("message")}");
                        return false;
                    }

                    var base64Chunk = resp.GetValueOrDefault("file_data")?.ToString();
                    if (string.IsNullOrEmpty(base64Chunk))
                    {
                        _logger.LogError("收到空块数据");
                        return false;
                    }

                    byte[] chunk = Convert.FromBase64String(base64Chunk);

                    await using var fs = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.Write);
                    fs.Seek(offset, SeekOrigin.Begin);
                    await fs.WriteAsync(chunk, 0, chunk.Length);

                    offset += chunk.Length;

                    bool isComplete = Convert.ToBoolean(resp.GetValueOrDefault("is_complete") ?? false);

                    // 进度回调
                    if (progressCallback != null && resp.TryGetValue("progress", out var progressValue))
                    {
                        if (double.TryParse(progressValue?.ToString(), out double progress))
                        {
                            progressCallback(fileId, progress, tempPath);
                        }
                    }

                    if (isComplete)
                    {
                        _logger.LogDebug($"分块下载完成: {fileId}");
                        break;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"分块下载异常: {ex.Message}");
                return false;
            }
        }


        // 校验文件完整性与合法性
        private bool ValidateFile(
            string tempPath,
            long expectedSize,
            Dictionary<string, string> checksums,
            string fileId)
        {
            // 大小校验
            if (new FileInfo(tempPath).Length != expectedSize) return false;
            // MD5 校验
            if (checksums != null && checksums.TryGetValue(fileId, out var expect)
                && ComputeMD5(tempPath) != expect) return false;
            // 图像合法性（仅 JPEG）
            return IsValidImage(tempPath);
        }

        // 计算文件 MD5
        private string ComputeMD5(string path)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToBase64String(md5.ComputeHash(stream));
        }

        // 简单检测 JPEG 文件头
        private bool IsValidImage(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                byte[] header = new byte[4];
                fs.Read(header, 0, 4);
                // JPEG: FF D8
                if (header[0] == 0xFF && header[1] == 0xD8) return true;
                // PNG: 89 50 4E 47
                if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47) return true;
                return false;
            }
            catch { return false; }
        }


        public async Task<Dictionary<string, object>> AddFriend(string friendUsername)
        {
            var req = new Dictionary<string, object>
            {
                { "type", "add_friend" },
                { "username", _username },
                { "friend", friendUsername },
                { "request_id", Guid.NewGuid().ToString() }
            };
            return await SendRequest(req);
        }

        public async Task<Dictionary<string, object>> ParseResponse(Dictionary<string, object> resp)
        {
            if (!resp.ContainsKey("chat_history") || resp["chat_history"] == null)
            {
                _logger.LogWarning("chat_history 字段缺失或为 null");
                return new Dictionary<string, object>
        {
            { "type", "chat_history" },
            { "data", new List<Dictionary<string, object>>() },
            { "request_id", resp.GetValueOrDefault("request_id") },
            { "errors", new List<Dictionary<string, object>> { new Dictionary<string, object> { { "error", "chat_history 缺失" } } } }
        };
            }

            var history = resp["chat_history"] as List<object> ?? (resp["chat_history"] as JArray)?.ToObject<List<object>>();
            if (history == null)
            {
                _logger.LogWarning($"chat_history 类型错误: {resp["chat_history"]?.GetType().Name}");
                return new Dictionary<string, object>
        {
            { "type", "chat_history" },
            { "data", new List<Dictionary<string, object>>() },
            { "request_id", resp.GetValueOrDefault("request_id") },
            { "errors", new List<Dictionary<string, object>> { new Dictionary<string, object> { { "error", "chat_history 类型错误" } } } }
        };
            }

            var parsed = new List<Dictionary<string, object>>();
            var errors = new List<Dictionary<string, object>>();
            foreach (var entryObj in history)
            {
                var entry = entryObj as Dictionary<string, object> ?? (entryObj as JObject)?.ToObject<Dictionary<string, object>>();
                if (entry == null)
                {
                    _logger.LogWarning($"聊天记录条目类型错误: {entryObj?.GetType().Name}");
                    errors.Add(new Dictionary<string, object> { { "entry", entryObj }, { "error", "条目类型错误" } });
                    continue;
                }

                try
                {
                    var parsedEntry = new Dictionary<string, object>
            {
                { "rowid", entry.GetValueOrDefault("rowid") },
                { "write_time", entry.GetValueOrDefault("write_time")?.ToString() },
                { "sender_username", entry.GetValueOrDefault("username")?.ToString() },
                { "message", entry.GetValueOrDefault("message")?.ToString() ?? "" },
                { "is_current_user", entry.GetValueOrDefault("username")?.ToString() == _username },
                { "reply_to", entry.GetValueOrDefault("reply_to") },
                { "reply_preview", entry.GetValueOrDefault("reply_preview") }
            };
                    if (entry.ContainsKey("attachment_type"))
                    {
                        parsedEntry["attachment_type"] = entry.GetValueOrDefault("attachment_type")?.ToString();
                        parsedEntry["file_id"] = entry.GetValueOrDefault("file_id")?.ToString();
                        parsedEntry["original_file_name"] = entry.GetValueOrDefault("original_file_name")?.ToString();
                        parsedEntry["thumbnail_path"] = entry.GetValueOrDefault("thumbnail_path")?.ToString();
                        parsedEntry["file_size"] = entry.GetValueOrDefault("file_size");
                        parsedEntry["duration"] = entry.GetValueOrDefault("duration");
                    }
                    parsed.Add(parsedEntry);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"解析聊天记录条目失败: {ex.Message}, 条目: {JsonConvert.SerializeObject(entry)}, StackTrace: {ex.StackTrace}");
                    errors.Add(new Dictionary<string, object> { { "entry", entry }, { "error", ex.Message } });
                }
            }

            var res = new Dictionary<string, object>
    {
        { "type", "chat_history" },
        { "data", parsed },
        { "request_id", resp.GetValueOrDefault("request_id") }
    };
            if (errors.Any())
            {
                _logger.LogWarning($"解析聊天记录包含错误: {JsonConvert.SerializeObject(errors)}");
                res["errors"] = errors;
            }
            return res;
        }

        public async Task ResetState()
        {
            _isAuthenticated = false;
            _username = null;
            CurrentFriend = null;
            Friends.Clear();
            UnreadMessages.Clear();
            _pendingRequests.Clear();
        }

        public async Task<Dictionary<string, object>> Logout()
        {
            var req = new Dictionary<string, object>
            {
                { "type", "exit" },
                { "username", _username },
                { "request_id", Guid.NewGuid().ToString() }
            };
            var resp = await SendRequest(req);
            await ResetState();
            return resp;
        }
        public async Task CloseConnection()
        {
            if (_isClosing || _clientSocket == null || !_isRunning)
            {
                _logger.LogDebug("连接已关闭或正在关闭，无需进一步操作。");
                return;
            }
            _isClosing = true;
            try
            {
                // 发送 exit 请求
                if (_clientSocket != null && _clientSocket.Connected && _stream != null)
                {
                    var req = new Dictionary<string, object>
            {
                { "type", "exit" },
                { "username", _username },
                { "request_id", Guid.NewGuid().ToString() }
            };
                    try
                    {
                        await _sendLock.WaitAsync();
                        try
                        {
                            byte[] ciphertext = Encrypt(req);
                            byte[] msg = PackMessage(ciphertext);
                            await _stream.WriteAsync(msg, 0, msg.Length);
                            _logger.LogDebug("已发送 exit 请求");
                        }
                        finally
                        {
                            _sendLock.Release();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"发送 exit 请求失败: {ex.Message}");
                    }
                }

                // 等待 StartReader 因服务器响应或连接断开而退出
                try
                {
                    if (_readerTask != null && !_readerTask.IsCompleted)
                    {
                        _logger.LogDebug("等待 StartReader 任务退出");
                        await Task.WhenAny(_readerTask, Task.Delay(2000)); // 最多等待 2 秒
                        if (!_readerTask.IsCompleted)
                        {
                            _logger.LogWarning("StartReader 未在 2 秒内退出，可能服务器未响应");
                        }
                    }
                    if (_registerTask != null && !_registerTask.IsCompleted)
                    {
                        _logger.LogDebug("等待 RegisterReader 任务退出");
                        await Task.WhenAny(_registerTask, Task.Delay(2000));
                    }
                    _readerTask = null;
                    _registerTask = null;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"等待 reader 任务完成失败: {ex.Message}");
                }

                // 清理连接资源
                try
                {
                    _isRunning = false;
                    if (_clientSocket != null)
                    {
                        try
                        {
                            _clientSocket.Close();
                            _clientSocket.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"清理 socket 失败: {ex.Message}");
                        }
                        _clientSocket = null;
                        _stream = null;
                        _logger.LogInformation("客户端连接已清理。");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"清理资源失败: {ex.Message}");
                }
            }
            finally
            {
                _isClosing = false;
                _isAuthenticated = false;
                _username = null;
            }
        }

        public void Dispose()
        {
            _clientSocket?.Dispose();
            _sendLock?.Dispose();
            _lock?.Dispose();
        }
    }
}
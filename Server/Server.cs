using ImageMagick;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MySql.Data.MySqlClient;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace Server
{
    public class Server
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<Server> _logger;
        private readonly UserRegister _userRegister;
        private readonly ConcurrentDictionary<string, Socket> _clients = new ConcurrentDictionary<string, Socket>();
        private readonly ConcurrentDictionary<string, UploadSession> _uploadSessions = new ConcurrentDictionary<string, UploadSession>();
        private readonly ConcurrentDictionary<(string, string), ConversationData> _conversations = new ConcurrentDictionary<(string, string), ConversationData>();
        private readonly object _clientsLock = new object();
        private readonly object _conversationsLock = new object();
        private readonly int _syncInterval = 600; // 同步间隔10分钟
        private readonly byte[] _encryptionKey;
        private readonly Aes _aes;
        private readonly string avatarDir = Path.Combine("user_data", "avatars");
        private readonly string fileDir = Path.Combine("user_data", "files");
        private readonly string imgDir = Path.Combine("user_data", "images");
        private readonly string vidDir = Path.Combine("user_data", "videos");
        private readonly string thumDir = Path.Combine("user_data", "thumbnails");

        public void StartServer()
        {
            _logger.LogDebug($"服务器启动，当前在线用户数: {_clients.Count}");
            _logger.LogDebug($"加载的会话数: {_conversations.Count}");
            try
            {
                var serverConfig = _configuration.GetSection("ServerConfig").Get<ServerConfig>();
                var listener = new TcpListener(IPAddress.Parse(serverConfig.Host), serverConfig.Port);
                listener.Start();
                _logger.LogInformation($"服务器启动在 {serverConfig.Host}:{serverConfig.Port}");

                // 初始化数据库和会话
                InitDb();
                LoadConversationsToMemory();

                // 开始接受客户端连接
                while (true)
                {
                    var client = listener.AcceptSocket();
                    var clientAddr = ((IPEndPoint)client.RemoteEndPoint).Address.ToString();
                    var clientPort = ((IPEndPoint)client.RemoteEndPoint).Port;
                    Task.Run(() => HandleClient(client, (clientAddr, clientPort)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"服务器启动失败: {ex.Message}");
            }
        }

        private class ConversationData
        {
            public MessageData LastMessage { get; set; }
            public DateTime LastUpdateTime { get; set; }
        }

        public Server(IConfiguration configuration)
        {
            _configuration = configuration;

            // 配置日志
            var factory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole(options =>
                {
                    // 启用自定义格式，包含时间戳
                    options.FormatterName = "customFormatter";
                })
                .AddConsoleFormatter<CustomConsoleFormatter, ConsoleFormatterOptions>()
                .SetMinimumLevel(LogLevel.Debug);
            });
            _logger = factory.CreateLogger<Server>();
            _userRegister = new UserRegister(factory.CreateLogger<UserRegister>());

            // 加载加密密钥
            var encryptionKeyBase64 = configuration["EncryptionKey"] ?? configuration["KEY:KEY"];
            if (string.IsNullOrEmpty(encryptionKeyBase64))
            {
                _logger.LogError("加密密钥未配置，请检查 appsettings.json 中的 EncryptionKey 或 KEY:KEY");
                throw new InvalidOperationException("加密密钥未配置");
            }
            try
            {
                _encryptionKey = Convert.FromBase64String(encryptionKeyBase64);
            }
            catch (FormatException ex)
            {
                _logger.LogError($"加密密钥格式无效: {ex.Message}");
                throw new InvalidOperationException("加密密钥必须是有效的 Base64 字符串");
            }
            _aes = Aes.Create();
            _aes.Key = _encryptionKey;
            _aes.Mode = CipherMode.CBC;
            _aes.Padding = PaddingMode.PKCS7;
        }

        public class CustomConsoleFormatter : ConsoleFormatter
        {
            private readonly ConsoleFormatterOptions _options;

            public CustomConsoleFormatter(IOptions<ConsoleFormatterOptions> options)
                : base("customFormatter")
            {
                _options = options.Value;
            }

            public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider scopeProvider, TextWriter textWriter)
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var logLevel = logEntry.LogLevel.ToString().ToUpper();
                var category = logEntry.Category;
                var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
                var exception = logEntry.Exception != null ? $"\nException: {logEntry.Exception}" : string.Empty;

                textWriter.WriteLine($"[{timestamp}] {logLevel} {category}: {message}{exception}");
            }
        }

        private MySqlConnection GetDbConnection()
        {
            try
            {
                var dbConfig = _configuration.GetSection("ServerConfig:DbConfig").Get<DbConfig>();
                var connectionString = $"Server={dbConfig.Host};User ID={dbConfig.User};Password={dbConfig.Password};Database={dbConfig.Database};Charset={dbConfig.Charset};";
                var conn = new MySqlConnection(connectionString);
                conn.Open();
                return conn;
            }
            catch (Exception ex)
            {
                _logger.LogError($"数据库连接失败: {ex.Message}");
                return null;
            }
        }

        private void InitDb()
        {
            using var conn = GetDbConnection();
            if (conn == null) return;

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS users (
                        username VARCHAR(255) PRIMARY KEY,
                        password VARCHAR(255),
                        avatars VARCHAR(255),
                        avatar_path VARCHAR(512),
                        names VARCHAR(255),
                        signs TEXT
                    ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci";
                cmd.ExecuteNonQuery();

                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS friends (
                        username VARCHAR(255),
                        friend VARCHAR(255),
                        Remarks VARCHAR(255),
                        PRIMARY KEY (username, friend),
                        FOREIGN KEY (username) REFERENCES users(username),
                        FOREIGN KEY (friend) REFERENCES users(username)
                    ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci";
                cmd.ExecuteNonQuery();

                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS messages (
                        id BIGINT AUTO_INCREMENT PRIMARY KEY,
                        sender VARCHAR(255),
                        receiver VARCHAR(255),
                        message TEXT,
                        write_time DATETIME,
                        attachment_type VARCHAR(50),
                        attachment_path VARCHAR(512),
                        original_file_name VARCHAR(255),
                        thumbnail_path VARCHAR(512),
                        file_size BIGINT,
                        duration FLOAT,
                        reply_to BIGINT,
                        reply_preview TEXT,
                        file_id VARCHAR(255),
                        FOREIGN KEY (sender) REFERENCES users(username),
                        FOREIGN KEY (receiver) REFERENCES users(username),
                        FOREIGN KEY (reply_to) REFERENCES messages(id)
                    ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci";
                cmd.ExecuteNonQuery();

                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS conversations (
                        username VARCHAR(255),
                        friend VARCHAR(255),
                        lastmessageid BIGINT,
                        lastupdatetime DATETIME,
                        PRIMARY KEY (username, friend),
                        FOREIGN KEY (username) REFERENCES users(username),
                        FOREIGN KEY (friend) REFERENCES users(username),
                        FOREIGN KEY (lastmessageid) REFERENCES messages(id)
                    ) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci";
                cmd.ExecuteNonQuery();

                _logger.LogInformation("数据库初始化成功");
            }
            catch (Exception ex)
            {
                _logger.LogError($"数据库初始化失败: {ex.Message}");
            }
        }

        private void LoadConversationsToMemory()
        {
            using var conn = GetDbConnection();
            if (conn == null)
            {
                _logger.LogError("无法加载会话数据到内存：数据库连接失败");
                return;
            }

            try
            {
                using var cmd = new MySqlCommand(@"
                    SELECT c.username, c.friend, m.id, m.sender, m.receiver, m.message, m.write_time, 
                           m.attachment_type, m.original_file_name, m.reply_to, m.reply_preview, c.lastupdatetime
                    FROM conversations c
                    LEFT JOIN messages m ON c.lastmessageid = m.id", conn);
                using var reader = cmd.ExecuteReader();
                lock (_conversationsLock)
                {
                    while (reader.Read())
                    {
                        var username = reader.GetString("username");
                        var friend = reader.GetString("friend");
                        var sortedKey = (username.CompareTo(friend) < 0) ? (username, friend) : (friend, username);
                        var lastMessage = reader.IsDBNull(reader.GetOrdinal("id")) ? null : new MessageData
                        {
                            RowId = reader.GetInt64("id"),
                            Sender = reader.GetString("sender"),
                            Receiver = reader.GetString("receiver"),
                            Message = reader.IsDBNull(reader.GetOrdinal("message")) ? null : reader.GetString("message"),
                            WriteTime = reader.GetDateTime("write_time").ToString("yyyy-MM-dd HH:mm:ss"),
                            AttachmentType = reader.IsDBNull(reader.GetOrdinal("attachment_type")) ? null : reader.GetString("attachment_type"),
                            OriginalFileName = reader.IsDBNull(reader.GetOrdinal("original_file_name")) ? null : reader.GetString("original_file_name"),
                            ReplyTo = reader.IsDBNull(reader.GetOrdinal("reply_to")) ? null : reader.GetInt64("reply_to"),
                            ReplyPreview = reader.IsDBNull(reader.GetOrdinal("reply_preview")) ? null : reader.GetString("reply_preview")
                        };
                        _conversations[sortedKey] = new ConversationData
                        {
                            LastMessage = lastMessage,
                            LastUpdateTime = reader.GetDateTime("lastupdatetime")
                        };
                        _logger.LogDebug($"加载会话 {username}-{friend} 到内存");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"加载会话数据失败: {ex.Message}");
            }
        }

        private Dictionary<string, object> GenerateReplyPreview(long replyToId)
        {
            using var conn = GetDbConnection();
            if (conn == null) return null;

            try
            {
                using var cmd = new MySqlCommand(@"
                    SELECT sender, message, attachment_type, original_file_name 
                    FROM messages 
                    WHERE id = @replyToId", conn);
                cmd.Parameters.AddWithValue("@replyToId", replyToId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    var content = (!reader.IsDBNull(reader.GetOrdinal("attachment_type")) && !reader.IsDBNull(reader.GetOrdinal("original_file_name")))
                        ? $"[{reader.GetString("attachment_type")}]: {reader.GetString("original_file_name")}"
                        : (!reader.IsDBNull(reader.GetOrdinal("message")) ? reader.GetString("message") : "空消息");
                    return new Dictionary<string, object>
                    {
                        { "sender", reader.GetString("sender") },
                        { "content", content }
                    };
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError($"生成回复预览失败: {ex.Message}");
                return null;
            }
        }

        private void SendResponse(Socket clientSock, Dictionary<string, object> response)
        {
            try
            {
                _logger.LogDebug($"发送响应给客户端 {((IPEndPoint)clientSock.RemoteEndPoint).Address}:{((IPEndPoint)clientSock.RemoteEndPoint).Port}");
                // 使用 UTF-8 编码序列化 JSON
                var plaintext = JsonConvert.SerializeObject(response, new JsonSerializerSettings
                {
                    StringEscapeHandling = StringEscapeHandling.Default
                });
                var plaintextBytes = Encoding.UTF8.GetBytes(plaintext); // 明确使用 UTF-8 编码

                var iv = RandomNumberGenerator.GetBytes(16);
                using var encryptor = _aes.CreateEncryptor(_aes.Key, iv);
                using var ms = new MemoryStream();
                ms.Write(iv, 0, iv.Length); // 写入初始化向量
                using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                {
                    cs.Write(plaintextBytes, 0, plaintextBytes.Length); // 写入 UTF-8 编码的字节
                    cs.FlushFinalBlock();
                }

                var ciphertext = ms.ToArray();
                var lengthHeader = BitConverter.GetBytes(ciphertext.Length).Reverse().ToArray(); // 大端序
                _logger.LogDebug($"发送响应给客户端，大小: {ciphertext.Length} 字节，内容: {plaintext}");
                var sendBuffer = lengthHeader.Concat(ciphertext).ToArray();
                clientSock.Send(sendBuffer);
                _logger.LogInformation("响应发送成功");
            }
            catch (Exception ex)
            {
                _logger.LogError($"发送响应失败: {ex.Message}");
                throw;
            }
        }

        private Dictionary<string, object> UpdateUserProfile(Dictionary<string, object> request, Socket clientSock)
        {
            var reqType = request["type"]?.ToString();
            var username = request["username"]?.ToString();
            var requestId = request["request_id"]?.ToString();
            var newSign = reqType == "update_sign" ? request["sign"]?.ToString() : null;
            var newNickname = reqType == "update_name" ? request["new_name"]?.ToString() : null;
            var fileDataB64 = reqType == "upload_avatar" ? request["file_data"]?.ToString() : null;

            using var conn = GetDbConnection();
            if (conn == null)
                return new Dictionary<string, object> { { "type", reqType }, { "status", "error" }, { "message", "数据库连接失败" }, { "request_id", requestId } };

            try
            {
                using var cmd = new MySqlCommand("SELECT username, avatars, avatar_path, signs FROM users WHERE username = @username", conn);
                cmd.Parameters.AddWithValue("@username", username);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                    return new Dictionary<string, object> { { "type", reqType }, { "status", "error" }, { "message", "用户不存在" }, { "request_id", requestId } };
                reader.Close();

                var updateFields = new List<string>();
                var updateValues = new List<object>();
                string avatarId = null; // 在方法顶部定义 avatarId

                if (reqType == "upload_avatar" && !string.IsNullOrEmpty(fileDataB64))
                {
                    Directory.CreateDirectory(avatarDir);
                    var originalFileName = $"{username}_avatar_{DateTime.Now:yyyyMMddHHmmssfffffff}.jpg";
                    var avatarPath = Path.Combine(avatarDir, originalFileName);
                    try
                    {
                        var fileData = Convert.FromBase64String(fileDataB64);
                        File.WriteAllBytes(avatarPath, fileData);
                        _logger.LogDebug($"头像保存成功: {avatarPath}");
                        updateFields.Add("avatars = @avatars");
                        updateFields.Add("avatar_path = @avatar_path");
                        updateValues.Add(originalFileName);
                        updateValues.Add(avatarPath);
                        avatarId = originalFileName; // 赋值 avatarId
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"头像保存失败: {ex.Message}");
                        return new Dictionary<string, object> { { "type", reqType }, { "status", "error" }, { "message", "头像保存失败" }, { "request_id", requestId } };
                    }
                }

                if (newSign != null)
                {
                    updateFields.Add("signs = @signs");
                    updateValues.Add(newSign);
                }
                if (newNickname != null)
                {
                    updateFields.Add("names = @names");
                    updateValues.Add(newNickname);
                }

                if (updateFields.Any())
                {
                    var sql = $"UPDATE users SET {string.Join(", ", updateFields)} WHERE username = @username";
                    using var updateCmd = new MySqlCommand(sql, conn);
                    for (int i = 0; i < updateFields.Count; i++)
                    {
                        var paramName = updateFields[i].Split('=')[1].Trim();
                        updateCmd.Parameters.AddWithValue(paramName, updateValues[i]);
                    }
                    updateCmd.Parameters.AddWithValue("@username", username);
                    _logger.LogDebug($"执行 SQL: {sql}");
                    _logger.LogDebug($"参数: {string.Join(", ", updateValues)}");
                    updateCmd.ExecuteNonQuery();
                    _logger.LogDebug($"更新用户信息成功: {reqType}，用户: {username}，新名字: {newNickname}");
                }
                else
                {
                    _logger.LogWarning($"没有需要更新的字段: {reqType}，用户: {username}");
                    return new Dictionary<string, object> { { "type", reqType }, { "status", "error" }, { "message", "没有需要更新的字段" }, { "request_id", requestId } };
                }

                var response = new Dictionary<string, object>
                {
                    { "type", reqType },
                    { "status", "success" },
                    { "message", "更新成功" },
                    { "request_id", requestId }
                };
                if (reqType == "upload_avatar" && !string.IsNullOrEmpty(avatarId))
                    response["avatar_id"] = avatarId; // 使用 avatarId

                PushFriendsUpdate(username, username);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError($"更新用户信息失败: {ex.Message}");
                return new Dictionary<string, object> { { "type", reqType }, { "status", "error" }, { "message", "数据库更新失败" }, { "request_id", requestId } };
            }
        }

        private Dictionary<string, object> UpdateFriendRemarks(Dictionary<string, object> request, Socket clientSock)
        {
            var username = request["username"]?.ToString();
            var friend = request["friend"]?.ToString();
            var remarks = request["remarks"]?.ToString();
            var requestId = request["request_id"]?.ToString();

            using var conn = GetDbConnection();
            if (conn == null)
                return new Dictionary<string, object> { { "type", "Update_Remarks" }, { "status", "error" }, { "message", "数据库连接失败" }, { "request_id", requestId } };

            try
            {
                using var cmd = new MySqlCommand("SELECT * FROM friends WHERE username = @username AND friend = @friend", conn);
                cmd.Parameters.AddWithValue("@username", username);
                cmd.Parameters.AddWithValue("@friend", friend);
                using var friendReader = cmd.ExecuteReader();
                if (!friendReader.Read())
                    return new Dictionary<string, object> { { "type", "Update_Remarks" }, { "status", "error" }, { "message", $"{friend} 不是您的好友" }, { "request_id", requestId } };
                friendReader.Close();

                cmd.CommandText = "SELECT names FROM users WHERE username = @friend";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@friend", friend);
                using var userReader = cmd.ExecuteReader();
                var friendName = userReader.Read() && !userReader.IsDBNull(userReader.GetOrdinal("names")) ? userReader.GetString("names") : friend;
                userReader.Close();

                string remarksToSet, displayRemarks, message;
                if (string.IsNullOrEmpty(remarks))
                {
                    remarksToSet = "";
                    displayRemarks = friendName;
                    message = $"已清除{friend}的备注";
                }
                else
                {
                    remarksToSet = remarks;
                    displayRemarks = remarks;
                    message = $"已将 {friend} 的备注更新为 {remarks}";
                }

                cmd.CommandText = "UPDATE friends SET Remarks = @remarks WHERE username = @username AND friend = @friend";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@remarks", remarksToSet);
                cmd.Parameters.AddWithValue("@username", username);
                cmd.Parameters.AddWithValue("@friend", friend);
                cmd.ExecuteNonQuery();
                _logger.LogDebug($"好友备注更新成功: {username} -> {friend}，备注: {remarksToSet}");
                return new Dictionary<string, object>
                {
                    { "type", "Update_Remarks" },
                    { "status", "success" },
                    { "message", message },
                    { "request_id", requestId },
                    { "friend", friend },
                    { "remarks", displayRemarks }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"更新好友备注失败: {ex.Message}");
                return new Dictionary<string, object> { { "type", "Update_Remarks" }, { "status", "error" }, { "message", "更新备注失败" }, { "request_id", requestId } };
            }
        }

        private List<Dictionary<string, object>> BuildFriendItem(string username, List<string> friendUsernames = null, MySqlConnection conn = null)
        {
            bool standaloneConn = conn == null;
            if (standaloneConn)
            {
                conn = GetDbConnection();
                if (conn == null)
                {
                    _logger.LogError("数据库连接失败，无法构建好友信息");
                    return new List<Dictionary<string, object>>();
                }
            }

            try
            {
                if (friendUsernames == null)
                {
                    using var friendCmd = new MySqlCommand("SELECT friend FROM friends WHERE username = @username", conn);
                    friendCmd.Parameters.AddWithValue("@username", username);
                    using var friendReader = friendCmd.ExecuteReader();
                    friendUsernames = new List<string>();
                    while (friendReader.Read())
                        friendUsernames.Add(friendReader.GetString("friend"));
                    friendReader.Close();
                }

                if (!friendUsernames.Any())
                    return new List<Dictionary<string, object>>();

                var placeholders = string.Join(",", Enumerable.Repeat("?", friendUsernames.Count));
                var query = $@"SELECT f.friend, f.Remarks, u.avatars, u.names, u.signs 
                      FROM friends f 
                      LEFT JOIN users u ON f.friend = u.username 
                      WHERE f.username = ? AND f.friend IN ({placeholders})";
                using var dataCmd = new MySqlCommand(query, conn);
                dataCmd.Parameters.AddWithValue("p0", username);
                for (int i = 0; i < friendUsernames.Count; i++)
                    dataCmd.Parameters.AddWithValue($"p{i + 1}", friendUsernames[i]);
                using var dataReader = dataCmd.ExecuteReader();
                var friendData = new Dictionary<string, Dictionary<string, object>>();
                while (dataReader.Read())
                {
                    friendData[dataReader.GetString("friend")] = new Dictionary<string, object>
            {
                { "friend", dataReader.GetString("friend") },
                { "Remarks", dataReader.IsDBNull(dataReader.GetOrdinal("Remarks")) ? null : dataReader.GetString("Remarks") },
                { "avatars", dataReader.IsDBNull(dataReader.GetOrdinal("avatars")) ? null : dataReader.GetString("avatars") },
                { "names", dataReader.IsDBNull(dataReader.GetOrdinal("names")) ? null : dataReader.GetString("names") },
                { "signs", dataReader.IsDBNull(dataReader.GetOrdinal("signs")) ? null : dataReader.GetString("signs") }
            };
                }
                dataReader.Close();

                var result = new List<Dictionary<string, object>>();
                lock (_conversationsLock)
                {
                    foreach (var friendUsername in friendUsernames)
                    {
                        if (!friendData.ContainsKey(friendUsername))
                            continue; // 跳过无数据的用户

                        var friendInfo = friendData[friendUsername];
                        var remarks = friendInfo["Remarks"]?.ToString();
                        var avatarId = friendInfo["avatars"]?.ToString() ?? "";
                        var userName = friendInfo["names"]?.ToString();
                        var sign = friendInfo["signs"]?.ToString() ?? "";
                        // 优先使用 Remarks，若为空则使用 names，若 names 也为空则使用 friendUsername
                        var displayName = !string.IsNullOrEmpty(remarks) ? remarks : !string.IsNullOrEmpty(userName) ? userName : friendUsername;

                        Dictionary<string, object> lastMessageData = null;
                        var sortedKey = (username.CompareTo(friendUsername) < 0) ? (username, friendUsername) : (friendUsername, username);
                        if (_conversations.TryGetValue(sortedKey, out var convoData) && convoData.LastMessage != null)
                        {
                            var lastMessage = convoData.LastMessage;
                            var content = GetConversationContent(lastMessage.AttachmentType, lastMessage.Message);
                            lastMessageData = new Dictionary<string, object>
                    {
                        { "sender", lastMessage.Sender },
                        { "content", content },
                        { "last_update_time", convoData.LastUpdateTime.ToString("yyyy-MM-dd HH:mm:ss") }
                    };
                        }

                        result.Add(new Dictionary<string, object>
                {
                    { "username", friendUsername },
                    { "avatar_id", avatarId },
                    { "name", displayName },
                    { "sign", sign },
                    { "online", _clients.ContainsKey(friendUsername) },
                    { "conversations", lastMessageData }
                });
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"构建好友信息失败: {ex.Message}");
                return new List<Dictionary<string, object>>();
            }
            finally
            {
                if (standaloneConn)
                    conn?.Dispose();
            }
        }

        private List<string> GetFriendUsernames(string username, bool includeSelf = false)
        {
            using var conn = GetDbConnection();
            if (conn == null)
            {
                _logger.LogError("数据库连接失败，无法获取好友列表");
                return new List<string>();
            }

            try
            {
                using var cmd = new MySqlCommand("SELECT friend FROM friends WHERE username = @username", conn);
                cmd.Parameters.AddWithValue("@username", username);
                using var reader = cmd.ExecuteReader();
                var friendUsernames = new List<string>();
                while (reader.Read())
                    friendUsernames.Add(reader.GetString("friend"));
                if (includeSelf)
                    friendUsernames.Add(username);
                return friendUsernames;
            }
            catch (Exception ex)
            {
                _logger.LogError($"获取好友列表失败: {ex.Message}");
                return new List<string>();
            }
        }

        private string GetConversationContent(string attachmentType, string message)
        {
            if (attachmentType == "file") return "[文件]";
            if (attachmentType == "image") return "[图片]";
            if (attachmentType == "video") return "[视频]";
            return message;
        }

        private void PushFriendsList(string username)
        {
            var friends = BuildFriendItem(username); // 直接获取列表

            lock (_clientsLock)
            {
                if (_clients.TryGetValue(username, out var client))
                {
                    var response = new Dictionary<string, object>
            {
                { "type", "friend_list_update" },
                { "status", "success" },
                { "friends", friends }
            };
                    SendResponse(client, response);
                    _logger.LogDebug($"推送好友列表给 {username}: {JsonConvert.SerializeObject(response)}");
                }
            }
        }

        private void PushFriendsUpdate(string username, string changedFriend = null)
        {
            if (!string.IsNullOrEmpty(changedFriend))
            {
                var friendList = BuildFriendItem(username, new List<string> { changedFriend });
                var friendItem = friendList.FirstOrDefault(f => f["username"].ToString() == changedFriend);
                if (friendItem != null)
                {
                    lock (_clientsLock)
                    {
                        if (_clients.TryGetValue(username, out var client))
                        {
                            var response = new Dictionary<string, object>
                    {
                        { "type", "friend_update" },
                        { "status", "success" },
                        { "friend", friendItem }
                    };
                            SendResponse(client, response);
                            _logger.LogDebug($"推送单个好友更新给 {username}: {JsonConvert.SerializeObject(response)}");
                        }
                    }
                }
            }

            var relatedUsers = GetFriendUsernames(username, true);
            foreach (var user in relatedUsers.Where(u => u != username))
            {
                var friendList = BuildFriendItem(user, new List<string> { username });
                var friendItem = friendList.FirstOrDefault(f => f["username"].ToString() == username);
                if (friendItem != null)
                {
                    lock (_clientsLock)
                    {
                        if (_clients.TryGetValue(user, out var client))
                        {
                            var response = new Dictionary<string, object>
                    {
                        { "type", "friend_update" },
                        { "status", "success" },
                        { "friend", friendItem }
                    };
                            SendResponse(client, response);
                            _logger.LogDebug($"推送单个好友更新给相关用户 {user}: {JsonConvert.SerializeObject(response)}");
                        }
                    }
                }
            }
        }

        private Dictionary<string, object> Authenticate(Dictionary<string, object> request, Socket clientSock)
        {
            var username = request["username"]?.ToString();
            var password = request["password"]?.ToString();
            var requestId = request["request_id"]?.ToString();

            using var conn = GetDbConnection();
            if (conn == null)
                return new Dictionary<string, object> { { "type", "authenticate" }, { "status", "error" }, { "message", "数据库连接失败" }, { "request_id", requestId } };

            try
            {
                using var cmd = new MySqlCommand("SELECT * FROM users WHERE username = @username AND password = @password", conn);
                cmd.Parameters.AddWithValue("@username", username);
                cmd.Parameters.AddWithValue("@password", password);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    lock (_clientsLock)
                    {
                        if (_clients.ContainsKey(username))
                        {
                            _logger.LogInformation($"用户 {username} 已经登录，拒绝重复登录");
                            return new Dictionary<string, object> { { "type", "authenticate" }, { "status", "fail" }, { "message", "该账号已登录" }, { "request_id", requestId } };
                        }
                        else
                        {
                            _clients.TryAdd(username, clientSock); // 使用线程安全的添加操作
                        }
                    }
                    _logger.LogInformation($"用户 {username} 认证成功");
                    return new Dictionary<string, object> { { "type", "authenticate" }, { "status", "success" }, { "message", "认证成功" }, { "request_id", requestId } };
                }
                else
                {
                    _logger.LogInformation($"用户 {username} 认证失败");
                    return new Dictionary<string, object> { { "type", "authenticate" }, { "status", "fail" }, { "message", "账号或密码错误" }, { "request_id", requestId } };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"数据库查询失败: {ex.Message}");
                return new Dictionary<string, object> { { "type", "authenticate" }, { "status", "error" }, { "message", "查询失败" }, { "request_id", requestId } };
            }
        }

        private Dictionary<string, object> SendMessage(Dictionary<string, object> request, Socket clientSock)
        {
            var fromUser = request["from"]?.ToString();
            var toUser = request["to"]?.ToString();
            var message = request["message"]?.ToString();
            var replyTo = request.ContainsKey("reply_to") && request["reply_to"] != null ? Convert.ToInt64(request["reply_to"]) : (long?)null;
            var currentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var requestId = request["request_id"]?.ToString();

            string replyPreview = null;
            if (replyTo.HasValue)
            {
                var replyPreviewData = GenerateReplyPreview(replyTo.Value);
                replyPreview = replyPreviewData != null
                    ? JsonConvert.SerializeObject(replyPreviewData)
                    : JsonConvert.SerializeObject(new { sender = "未知用户", content = "消息不可用" });
            }

            using var conn = GetDbConnection();
            if (conn == null)
                return new Dictionary<string, object> { { "type", "send_message" }, { "status", "error" }, { "message", "数据库连接失败" }, { "request_id", requestId } };

            try
            {
                using var cmd = new MySqlCommand(@"
                    INSERT INTO messages (sender, receiver, message, write_time, reply_to, reply_preview)
                    VALUES (@sender, @receiver, @message, @write_time, @reply_to, @reply_preview)", conn);
                cmd.Parameters.AddWithValue("@sender", fromUser);
                cmd.Parameters.AddWithValue("@receiver", toUser);
                cmd.Parameters.AddWithValue("@message", message);
                cmd.Parameters.AddWithValue("@write_time", currentTime);
                cmd.Parameters.AddWithValue("@reply_to", replyTo.HasValue ? replyTo : DBNull.Value);
                cmd.Parameters.AddWithValue("@reply_preview", replyPreview ?? (object)DBNull.Value);
                cmd.ExecuteNonQuery();
                long rowId = cmd.LastInsertedId;

                var lastMessage = new MessageData
                {
                    RowId = rowId,
                    Sender = fromUser,
                    Receiver = toUser,
                    Message = message,
                    WriteTime = currentTime,
                    AttachmentType = null,
                    OriginalFileName = null,
                    ReplyTo = replyTo,
                    ReplyPreview = replyPreview
                };
                UpdateConversationLastMessage(fromUser, toUser, lastMessage);

                var conversations = message;

                var pushResponse = new Dictionary<string, object>
                {
                    { "type", "new_message" },
                    { "from", fromUser },
                    { "to", toUser },
                    { "message", message },
                    { "write_time", currentTime },
                    { "reply_to", replyTo },
                    { "reply_preview", replyPreview },
                    { "rowid", rowId },
                    { "conversations", conversations }
                };
                lock (_clientsLock)
                {
                    if (_clients.TryGetValue(toUser, out var client))
                        SendResponse(client, pushResponse);
                }

                return new Dictionary<string, object>
                {
                    { "type", "send_message" },
                    { "status", "success" },
                    { "message", $"消息已发送给 {toUser}" },
                    { "request_id", requestId },
                    { "rowid", rowId },
                    { "reply_to", replyTo },
                    { "reply_preview", replyPreview },
                    { "conversations", conversations },
                    { "write_time", currentTime }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"发送消息失败: {ex.Message}");
                return new Dictionary<string, object> { { "type", "send_message" }, { "status", "error" }, { "message", "消息发送失败" }, { "request_id", requestId } };
            }
        }

        private Dictionary<string, object> SendMedia(Dictionary<string, object> request, Socket clientSock)
        {
            _logger.LogDebug($"收到 send_media 请求: {JsonConvert.SerializeObject(request)}");
            var fromUser = request["from"]?.ToString();
            var toUser = request["to"]?.ToString();
            var originalFileName = request["file_name"]?.ToString();
            var fileType = request["file_type"]?.ToString();
            var message = request.ContainsKey("message") ? request["message"]?.ToString() : "";
            var replyTo = request.ContainsKey("reply_to") && request["reply_to"] != null ? Convert.ToInt64(request["reply_to"]) : (long?)null;
            var requestId = request["request_id"]?.ToString();
            var fileDataB64 = request.ContainsKey("file_data") ? request["file_data"]?.ToString() : "";
            var totalSize = request.ContainsKey("total_size") ? Convert.ToInt64(request["total_size"]) : 0;

            string UploadDir;
            switch (fileType)
            {
                case "file": UploadDir = fileDir; break;
                case "image": UploadDir = imgDir; break;
                case "video": UploadDir = vidDir; break;
                default: UploadDir = fileDir; break;
            }
            Directory.CreateDirectory(UploadDir);

            if (!_uploadSessions.ContainsKey(requestId))
            {
                var sessionFileName = $"{DateTime.Now:yyyyMMddHHmmssfffffff}_{originalFileName}";
                var sessionFilePath = Path.Combine(UploadDir, sessionFileName);
                _uploadSessions[requestId] = new UploadSession
                {
                    FilePath = sessionFilePath,
                    TotalSize = totalSize,
                    ReceivedSize = 0,
                    UniqueFileName = sessionFileName
                };
            }
            var session = _uploadSessions[requestId];
            var filePath = session.FilePath;
            var uniqueFileName = session.UniqueFileName;

            if (!string.IsNullOrEmpty(fileDataB64))
            {
                try
                {
                    var fileData = Convert.FromBase64String(fileDataB64);
                    using (var fs = new FileStream(filePath, FileMode.Append, FileAccess.Write))
                        fs.Write(fileData, 0, fileData.Length);
                    session.ReceivedSize += fileData.Length;
                    _logger.LogDebug($"写入文件: {filePath}，大小: {fileData.Length}");
                    return new Dictionary<string, object>
                    {
                        { "type", "send_media" },
                        { "status", "success" },
                        { "message", "分块接收中" },
                        { "request_id", requestId }
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError($"文件写入失败: {ex.Message}");
                    return new Dictionary<string, object>
                    {
                        { "type", "send_media" },
                        { "status", "error" },
                        { "message", "文件写入失败" },
                        { "request_id", requestId }
                    };
                }
            }
            else
            {
                long fileSize = session.ReceivedSize;
                string thumbnailPath = "";
                string thumbnailDataB64 = "";
                float duration = 0;

                if (!File.Exists(filePath))
                {
                    _logger.LogError($"文件未找到: {filePath}");
                    return new Dictionary<string, object>
                    {
                        { "type", "send_media" },
                        { "status", "error" },
                        { "message", "文件保存失败，路径未找到" },
                        { "request_id", requestId }
                    };
                }

                if (fileType == "image")
                {
                    try
                    {
                        using var image = Image.FromFile(filePath);
                        int originalWidth = image.Width;
                        int originalHeight = image.Height;

                        double scale = (originalWidth < originalHeight)
                            ? 200.0 / originalWidth
                            : 200.0 / originalHeight;

                        int targetWidth = (int)(originalWidth * scale);
                        int targetHeight = (int)(originalHeight * scale);

                        using var resized = new Bitmap(image, targetWidth, targetHeight);
                        var thumbnailFilename = $"thumb_{uniqueFileName}.jpg";
                        thumbnailPath = Path.Combine(UploadDir, thumbnailFilename);
                        resized.Save(thumbnailPath, ImageFormat.Jpeg);
                        _logger.LogDebug($"生成图片缩略图: {thumbnailPath}");

                        thumbnailDataB64 = Convert.ToBase64String(File.ReadAllBytes(thumbnailPath));
                        if (!File.Exists(thumbnailPath))
                            _logger.LogError($"缩略图文件未生成: {thumbnailPath}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"生成图片缩略图失败: {ex.Message}, 文件路径: {filePath}");
                        thumbnailPath = "";
                    }
                }
                else if (fileType == "video")
                {
                    try
                    {
                        var settings = new MagickReadSettings();
                        using var collection = new MagickImageCollection(filePath, settings);
                        if (collection.Any())
                        {
                            var frame = collection[0]; // 取第一帧
                            uint width = frame.Width;
                            uint height = frame.Height;

                            int targetShort = 200;
                            int newWidth, newHeight;

                            if (width <= height)
                            {
                                newWidth = targetShort;
                                newHeight = (int)(height * (targetShort / (double)width));
                            }
                            else
                            {
                                newHeight = targetShort;
                                newWidth = (int)(width * (targetShort / (double)height));
                            }

                            frame.Resize((uint)newWidth, (uint)newHeight); // ✅ 强制转换为 uint

                            var thumbnailFilename = $"thumb_{uniqueFileName}.jpg";
                            thumbnailPath = Path.Combine(UploadDir, thumbnailFilename);
                            frame.Format = MagickFormat.Jpeg;
                            frame.Write(thumbnailPath);

                            _logger.LogDebug($"生成视频缩略图: {thumbnailPath}");
                            thumbnailDataB64 = Convert.ToBase64String(File.ReadAllBytes(thumbnailPath));
                            duration = 0; // 后面可以再补
                        }

                        else
                        {
                            _logger.LogError($"视频文件无有效帧: {filePath}");
                            thumbnailPath = "";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"生成视频缩略图失败: {ex.Message}, 文件路径: {filePath}");
                        thumbnailPath = "";
                        duration = 0;
                    }
                }

                string replyPreview = null;
                if (replyTo.HasValue)
                {
                    var replyPreviewData = GenerateReplyPreview(replyTo.Value);
                    replyPreview = replyPreviewData != null
                        ? JsonConvert.SerializeObject(replyPreviewData)
                        : JsonConvert.SerializeObject(new { sender = "未知用户", content = "消息不可用" });
                }

                using var conn = GetDbConnection();
                if (conn == null)
                    return new Dictionary<string, object>
                    {
                        { "type", "send_media" },
                        { "status", "error" },
                        { "message", "数据库连接失败" },
                        { "request_id", requestId }
                    };

                try
                {
                    var currentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    using var cmd = new MySqlCommand(@"
                        INSERT INTO messages (sender, receiver, message, write_time, attachment_type, attachment_path, 
                                            original_file_name, thumbnail_path, file_size, duration, reply_to, 
                                            reply_preview, file_id)
                        VALUES (@sender, @receiver, @message, @write_time, @attachment_type, @attachment_path, 
                                @original_file_name, @thumbnail_path, @file_size, @duration, @reply_to, 
                                @reply_preview, @file_id)", conn);
                    cmd.Parameters.AddWithValue("@sender", fromUser);
                    cmd.Parameters.AddWithValue("@receiver", toUser);
                    cmd.Parameters.AddWithValue("@message", message);
                    cmd.Parameters.AddWithValue("@write_time", currentTime);
                    cmd.Parameters.AddWithValue("@attachment_type", fileType);
                    cmd.Parameters.AddWithValue("@attachment_path", filePath);
                    cmd.Parameters.AddWithValue("@original_file_name", originalFileName);
                    cmd.Parameters.AddWithValue("@thumbnail_path", thumbnailPath);
                    cmd.Parameters.AddWithValue("@file_size", fileSize);
                    cmd.Parameters.AddWithValue("@duration", duration);
                    cmd.Parameters.AddWithValue("@reply_to", replyTo.HasValue ? replyTo : DBNull.Value);
                    cmd.Parameters.AddWithValue("@reply_preview", replyPreview ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@file_id", uniqueFileName);
                    cmd.ExecuteNonQuery();
                    long rowId = cmd.LastInsertedId;

                    var lastMessage = new MessageData
                    {
                        RowId = rowId,
                        Sender = fromUser,
                        Receiver = toUser,
                        Message = message,
                        WriteTime = currentTime,
                        AttachmentType = fileType,
                        OriginalFileName = originalFileName,
                        ReplyTo = replyTo,
                        ReplyPreview = replyPreview,
                        FileId = uniqueFileName
                    };
                    UpdateConversationLastMessage(fromUser, toUser, lastMessage);

                    string conversations;
                    switch (fileType)
                    {
                        case "file": conversations = "[文件]"; break;
                        case "image": conversations = "[图片]"; break;
                        case "video": conversations = "[视频]"; break;
                        default: conversations = message; break;
                    }

                    var pushResponse = new Dictionary<string, object>
                    {
                        { "type", "new_media" },
                        { "status", "success" },
                        { "from", fromUser },
                        { "to", toUser },
                        { "message", message },
                        { "original_file_name", originalFileName },
                        { "file_type", fileType },
                        { "file_id", uniqueFileName },
                        { "write_time", currentTime },
                        { "file_size", fileSize },
                        { "duration", duration },
                        { "reply_to", replyTo },
                        { "reply_preview", replyPreview },
                        { "rowid", rowId },
                        { "conversations", conversations },
                        { "thumbnail_data", fileType == "image" || fileType == "video" ? thumbnailDataB64 : "" }
                    };
                    lock (_clientsLock)
                    {
                        if (_clients.TryGetValue(toUser, out var client))
                            SendResponse(client, pushResponse);
                    }

                    _uploadSessions.TryRemove(requestId, out _);
                    return new Dictionary<string, object>
                    {
                        { "type", "send_media" },
                        { "status", "success" },
                        { "message", $"{fileType} 已发送给 {toUser}" },
                        { "request_id", requestId },
                        { "file_id", uniqueFileName },
                        { "write_time", currentTime },
                        { "duration", duration },
                        { "rowid", rowId },
                        { "reply_to", replyTo },
                        { "reply_preview", replyPreview },
                        { "text_message", message },
                        { "conversations", conversations },
                        { "thumbnail_data", fileType == "image" || fileType == "video" ? thumbnailDataB64 : "" }
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError($"保存媒体消息失败: {ex.Message}");
                    return new Dictionary<string, object>
                    {
                        { "type", "send_media" },
                        { "status", "error" },
                        { "message", "保存失败" },
                        { "request_id", requestId }
                    };
                }
            }
        }

        private Dictionary<string, object> DeleteMessages(Dictionary<string, object> request, Socket clientSock)
        {
            var username = request["username"]?.ToString();

            // 使用JArray来处理rowids
            var rowIds = new List<long>();

            if (request.ContainsKey("rowids") && request["rowids"] is JArray array)
            {
                rowIds = array.Select(t => t.Value<long>()).ToList();
            }
            else if (request.ContainsKey("rowid") && request["rowid"] != null)
            {
                rowIds.Add(Convert.ToInt64(request["rowid"]));
            }

            var requestId = request["request_id"]?.ToString();

            if (!rowIds.Any())
                return new Dictionary<string, object> { { "type", "messages_deleted" }, { "status", "error" }, { "message", "未指定要删除的消息" }, { "request_id", requestId } };

            using var conn = GetDbConnection();
            if (conn == null)
                return new Dictionary<string, object> { { "type", "messages_deleted" }, { "status", "error" }, { "message", "数据库连接失败" }, { "request_id", requestId } };

            try
            {
                var placeholders = string.Join(",", rowIds.Select(_ => "?"));
                using var cmd = new MySqlCommand($"SELECT id, sender, receiver FROM messages WHERE id IN ({placeholders}) AND (sender = ? OR receiver = ?)", conn);
                foreach (var rowId in rowIds)
                    cmd.Parameters.AddWithValue($"p{cmd.Parameters.Count}", rowId);
                cmd.Parameters.AddWithValue($"p{cmd.Parameters.Count}", username);
                cmd.Parameters.AddWithValue($"p{cmd.Parameters.Count}", username);
                var messagesToDelete = new List<Dictionary<string, object>>();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        messagesToDelete.Add(new Dictionary<string, object>
                {
                    { "id", reader.GetInt64("id") },
                    { "sender", reader.GetString("sender") },
                    { "receiver", reader.GetString("receiver") }
                });
                }

                if (!messagesToDelete.Any())
                    return new Dictionary<string, object> { { "type", "messages_deleted" }, { "status", "error" }, { "message", "消息不存在或无权限" }, { "request_id", requestId } };

                cmd.Parameters.Clear();
                cmd.CommandText = $"DELETE FROM conversations WHERE lastmessageid IN ({placeholders})";
                foreach (var rowId in rowIds)
                    cmd.Parameters.AddWithValue($"p{cmd.Parameters.Count}", rowId);
                cmd.ExecuteNonQuery();

                cmd.Parameters.Clear();
                cmd.CommandText = $"DELETE FROM messages WHERE id IN ({placeholders})";
                foreach (var rowId in rowIds)
                    cmd.Parameters.AddWithValue($"p{cmd.Parameters.Count}", rowId);
                cmd.ExecuteNonQuery();

                var affectedPairs = messagesToDelete.Select(msg => msg["sender"].ToString().CompareTo(msg["receiver"].ToString()) < 0
                    ? (msg["sender"].ToString(), msg["receiver"].ToString())
                    : (msg["receiver"].ToString(), msg["sender"].ToString())).Distinct().ToList();
                string conversationsContent = "";
                var writeTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                foreach (var (user1, user2) in affectedPairs)
                {
                    cmd.Parameters.Clear();
                    cmd.CommandText = @"
                SELECT id, sender, receiver, message, write_time, attachment_type, original_file_name, 
                       reply_to, reply_preview FROM messages 
                WHERE (sender = @user1 AND receiver = @user2) OR (sender = @user2 AND receiver = @user1) 
                ORDER BY write_time DESC LIMIT 1";
                    cmd.Parameters.AddWithValue("@user1", user1);
                    cmd.Parameters.AddWithValue("@user2", user2);
                    MessageData lastMessage = null;
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            conversationsContent = reader.GetString("attachment_type") switch
                            {
                                "file" => "[文件]",
                                "image" => "[图片]",
                                "video" => "[视频]",
                                _ => !reader.IsDBNull(reader.GetOrdinal("message")) ? reader.GetString("message") : ""
                            };
                            writeTime = reader.GetDateTime("write_time").ToString("yyyy-MM-dd HH:mm:ss");
                            lastMessage = new MessageData
                            {
                                RowId = reader.GetInt64("id"),
                                Sender = reader.GetString("sender"),
                                Receiver = reader.GetString("receiver"),
                                Message = reader.IsDBNull(reader.GetOrdinal("message")) ? null : reader.GetString("message"),
                                WriteTime = writeTime,
                                AttachmentType = reader.IsDBNull(reader.GetOrdinal("attachment_type")) ? null : reader.GetString("attachment_type"),
                                OriginalFileName = reader.IsDBNull(reader.GetOrdinal("original_file_name")) ? null : reader.GetString("original_file_name"),
                                ReplyTo = reader.IsDBNull(reader.GetOrdinal("reply_to")) ? null : reader.GetInt64("reply_to"),
                                ReplyPreview = reader.IsDBNull(reader.GetOrdinal("reply_preview")) ? null : reader.GetString("reply_preview")
                            };
                        }
                    }

                    if (lastMessage != null)
                    {
                        UpdateConversationLastMessage(user1, user2, lastMessage);
                    }
                    else
                    {
                        conversationsContent = "";
                        writeTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        lock (_conversationsLock)
                        {
                            var sortedKey = (user1.CompareTo(user2) < 0) ? (user1, user2) : (user2, user1);
                            _conversations.TryRemove(sortedKey, out _);
                        }
                        cmd.Parameters.Clear();
                        cmd.CommandText = @"
                    INSERT INTO conversations (username, friend, lastmessageid, lastupdatetime)
                    VALUES (@username, @friend, NULL, @lastupdatetime)
                    ON DUPLICATE KEY UPDATE lastmessageid = NULL, lastupdatetime = @lastupdatetime";
                        cmd.Parameters.AddWithValue("@username", user1);
                        cmd.Parameters.AddWithValue("@friend", user2);
                        cmd.Parameters.AddWithValue("@lastupdatetime", writeTime);
                        cmd.ExecuteNonQuery();
                    }

                    var otherUser = user1 == username ? user2 : user1;
                    var pushPayload = new Dictionary<string, object>
            {
                { "type", "deleted_messages" },
                { "from", username },
                { "to", otherUser },
                { "deleted_rowids", messagesToDelete.Select(m => m["id"]).ToList() },
                { "conversations", conversationsContent },
                { "write_time", writeTime },
                { "show_floating_label", false }
            };
                    lock (_clientsLock)
                    {
                        if (_clients.TryGetValue(otherUser, out var client) && otherUser != username)
                            SendResponse(client, pushPayload);
                    }
                }

                var returnData = new Dictionary<string, object>
        {
            { "type", "messages_deleted" },
            { "status", "success" },
            { "request_id", requestId },
            { "to", username },
            { "deleted_rowids", messagesToDelete.Select(m => m["id"]).ToList() },
            { "conversations", conversationsContent },
            { "write_time", writeTime },
            { "show_floating_label", true }
        };
                _logger.LogDebug($"delete_message返回: {JsonConvert.SerializeObject(returnData)}");
                return returnData;
            }
            catch (Exception ex)
            {
                _logger.LogError($"删除消息失败: {ex.Message}");
                return new Dictionary<string, object> { { "type", "messages_deleted" }, { "status", "error" }, { "message", "删除消息失败" }, { "request_id", requestId } };
            }
        }

        private List<Dictionary<string, object>> GetConversations(string username)
        {
            var conversationsList = new List<Dictionary<string, object>>();
            lock (_conversationsLock)
            {
                foreach (var ((user1, user2), data) in _conversations)
                {
                    if (user1 == username || user2 == username)
                    {
                        var otherUser = user1 == username ? user2 : user1;
                        conversationsList.Add(new Dictionary<string, object>
                        {
                            { "with_user", otherUser },
                            { "last_message", data.LastMessage },
                            { "last_update_time", data.LastUpdateTime.ToString("yyyy-MM-dd HH:mm:ss") }
                        });
                    }
                }
            }
            conversationsList.Sort((a, b) => DateTime.Parse(b["last_update_time"].ToString()).CompareTo(DateTime.Parse(a["last_update_time"].ToString())));
            return conversationsList;
        }

        private void UpdateConversationLastMessage(string username, string friend, MessageData message)
        {
            var sortedKey = (username.CompareTo(friend) < 0) ? (username, friend) : (friend, username);
            var writeTime = DateTime.Parse(message.WriteTime);
            lock (_conversationsLock)
            {
                _conversations[sortedKey] = new ConversationData
                {
                    LastMessage = message,
                    LastUpdateTime = writeTime
                };
            }
            using var conn = GetDbConnection();
            if (conn == null) return;
            try
            {
                using var cmd = new MySqlCommand(@"
                    INSERT INTO conversations (username, friend, lastmessageid, lastupdatetime)
                    VALUES (@username, @friend, @lastmessageid, @lastupdatetime)
                    ON DUPLICATE KEY UPDATE lastmessageid = @lastmessageid, lastupdatetime = @lastupdatetime", conn);
                cmd.Parameters.AddWithValue("@username", sortedKey.Item1);
                cmd.Parameters.AddWithValue("@friend", sortedKey.Item2);
                cmd.Parameters.AddWithValue("@lastmessageid", message.RowId);
                cmd.Parameters.AddWithValue("@lastupdatetime", message.WriteTime);
                cmd.ExecuteNonQuery();
                _logger.LogDebug($"会话 {username}-{friend} 已立即同步到数据库");
            }
            catch (Exception ex)
            {
                _logger.LogError($"同步会话 {username}-{friend} 到数据库失败: {ex.Message}");
            }
        }

        private Dictionary<string, object> GetChatHistoryPaginated(Dictionary<string, object> rawRequest, Socket clientSock)
        {
            RequestModel.ChatHistory request;
            try
            {
                var json = JsonConvert.SerializeObject(rawRequest);
                request = JsonConvert.DeserializeObject<RequestModel.ChatHistory>(json);
            }
            catch (Exception ex)
            {
                _logger.LogError($"请求反序列化失败: {ex.Message}");
                var resp = new Dictionary<string, object>
        {
            { "type", "chat_history" },
            { "status", "error" },
            { "message", "请求格式错误" },
            { "chat_history", new List<object>() },
            { "request_id", rawRequest["request_id"]?.ToString() }
        };
                SendResponse(clientSock, resp);
                return resp;
            }

            var username = request.Username;
            var friend = request.Friend;
            var page = request.Page;
            var pageSize = request.PageSize;
            var requestId = request.RequestId;

            using var conn = GetDbConnection();
            if (conn == null)
            {
                var resp = new Dictionary<string, object>
        {
            { "type", "chat_history" },
            { "status", "error" },
            { "message", "数据库连接失败" },
            { "chat_history", new List<object>() },
            { "request_id", requestId }
        };
                SendResponse(clientSock, resp);
                return resp;
            }

            try
            {
                var offset = (page - 1) * pageSize;
                using var cmd = new MySqlCommand(@"
            SELECT id AS rowid, write_time, sender, receiver, message, attachment_type, attachment_path, 
                   original_file_name, thumbnail_path, file_size, duration, reply_to, reply_preview, file_id
            FROM messages
            WHERE (sender = @username AND receiver = @friend) OR (sender = @friend AND receiver = @username)
            ORDER BY write_time DESC, id DESC
            LIMIT @pageSize OFFSET @offset", conn);
                cmd.Parameters.AddWithValue("@username", username);
                cmd.Parameters.AddWithValue("@friend", friend);
                cmd.Parameters.AddWithValue("@pageSize", pageSize);
                cmd.Parameters.AddWithValue("@offset", offset);
                var history = new List<Dictionary<string, object>>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var record = new Dictionary<string, object>
            {
                { "rowid", reader.GetInt64("rowid") },
                { "write_time", reader.GetDateTime("write_time").ToString("yyyy-MM-dd HH:mm:ss") },
                { "username", reader.GetString("sender") },
                { "friend_username", reader.GetString("receiver") },
                { "message", reader.IsDBNull(reader.GetOrdinal("message")) ? null : reader.GetString("message") },
                { "reply_to", reader.IsDBNull(reader.GetOrdinal("reply_to")) ? null : reader.GetInt64("reply_to") },
                { "reply_preview", reader.IsDBNull(reader.GetOrdinal("reply_preview")) ? null : reader.GetString("reply_preview") }
            };
                    if (!reader.IsDBNull(reader.GetOrdinal("attachment_type")))
                    {
                        record["attachment_type"] = reader.GetString("attachment_type");
                        record["file_id"] = !reader.IsDBNull(reader.GetOrdinal("file_id")) ? reader.GetString("file_id") : Path.GetFileName(reader.GetString("attachment_path"));
                        record["original_file_name"] = reader.IsDBNull(reader.GetOrdinal("original_file_name")) ? null : reader.GetString("original_file_name");
                        record["file_size"] = reader.IsDBNull(reader.GetOrdinal("file_size")) ? null : reader.GetInt64("file_size");
                        record["duration"] = reader.IsDBNull(reader.GetOrdinal("duration")) ? null : reader.GetFloat("duration");
                    }
                    history.Add(record);
                }
                var resp = new Dictionary<string, object>
        {
            { "type", "chat_history" },
            { "status", "success" },
            { "chat_history", history },
            { "request_id", requestId }
        };
                SendResponse(clientSock, resp);
                return resp;
            }
            catch (Exception ex)
            {
                _logger.LogError($"查询聊天记录失败: {ex.Message}");
                var resp = new Dictionary<string, object>
        {
            { "type", "chat_history" },
            { "status", "error" },
            { "message", "查询失败" },
            { "chat_history", new List<object>() },
            { "request_id", requestId }
        };
                SendResponse(clientSock, resp);
                return resp;
            }
        }

        private Dictionary<string, object> AddFriend(Dictionary<string, object> request, Socket clientSock)
        {
            var username = request["username"]?.ToString();
            var friend = request["friend"]?.ToString();
            var requestId = request["request_id"]?.ToString();

            using var conn = GetDbConnection();
            if (conn == null)
                return new Dictionary<string, object> { { "type", "add_friend" }, { "status", "error" }, { "message", "数据库连接失败" }, { "request_id", requestId } };

            try
            {
                // 检查目标用户是否存在
                using var cmd = new MySqlCommand("SELECT * FROM users WHERE username = @friend", conn);
                cmd.Parameters.AddWithValue("@friend", friend);
                using var userReader = cmd.ExecuteReader();
                if (!userReader.Read())
                    return new Dictionary<string, object> { { "type", "add_friend" }, { "status", "error" }, { "message", $"用户 {friend} 不存在，无法添加" }, { "request_id", requestId } };
                userReader.Close();

                // 检查是否已经是好友
                cmd.CommandText = "SELECT * FROM friends WHERE username = @username AND friend = @friend";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@username", username);
                cmd.Parameters.AddWithValue("@friend", friend);
                using var friendReader = cmd.ExecuteReader();
                if (friendReader.Read())
                    return new Dictionary<string, object> { { "type", "add_friend" }, { "status", "fail" }, { "message", $"{friend} 已是您的好友" }, { "request_id", requestId } };
                friendReader.Close();

                // 添加好友关系
                cmd.CommandText = "INSERT INTO friends (username, friend) VALUES (@username, @friend)";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@username", username);
                cmd.Parameters.AddWithValue("@friend", friend);
                cmd.ExecuteNonQuery();

                // 添加反向好友关系
                cmd.CommandText = "INSERT INTO friends (username, friend) VALUES (@username, @friend)";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@username", friend);
                cmd.Parameters.AddWithValue("@friend", username);
                cmd.ExecuteNonQuery();

                var response = new Dictionary<string, object>
                {
                    { "type", "add_friend" },
                    { "status", "success" },
                    { "message", $"{friend} 已添加为您的好友" },
                    { "request_id", requestId }
                };
                PushFriendsUpdate(username, friend);
                PushFriendsUpdate(friend, username);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError($"添加好友失败: {ex.Message}");
                return new Dictionary<string, object> { { "type", "add_friend" }, { "status", "error" }, { "message", "添加好友失败" }, { "request_id", requestId } };
            }
        }

        private async Task HandleClient(Socket clientSock, (string ip, int port) clientAddr)
        {
            _logger.LogInformation($"客户端 {clientAddr.ip}:{clientAddr.port} 已连接");
            string loggedInUser = null;
            bool isRegistering = false; // 标记是否处于注册流程
            try
            {
                while (true)
                {
                    var header = new byte[4];
                    int bytesRead = await Task.Run(() => clientSock.Receive(header));
                    if (bytesRead == 0)
                    {
                        _logger.LogInformation($"客户端 {clientAddr.ip}:{clientAddr.port} 断开连接（未收到头部）");
                        break;
                    }
                    var msgLength = BitConverter.ToInt32(header.Reverse().ToArray(), 0);

                    var encryptedData = new byte[msgLength];
                    bytesRead = await Task.Run(() => clientSock.Receive(encryptedData));
                    if (bytesRead == 0)
                    {
                        _logger.LogInformation($"客户端 {clientAddr.ip}:{clientAddr.port} 断开连接（未收到数据）");
                        break;
                    }

                    Dictionary<string, object> request;
                    try
                    {
                        using var ms = new MemoryStream(encryptedData);
                        var iv = new byte[16];
                        ms.Read(iv, 0, 16);
                        using var decryptor = _aes.CreateDecryptor(_aes.Key, iv);
                        using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
                        using var sr = new StreamReader(cs);
                        var decryptedData = sr.ReadToEnd();
                        request = JsonConvert.DeserializeObject<Dictionary<string, object>>(decryptedData);
                        _logger.LogDebug($"收到请求：{JsonConvert.SerializeObject(request)}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"数据解密或JSON解析错误：{ex.Message}");
                        SendResponse(clientSock, new Dictionary<string, object>
                {
                    { "status", "error" },
                    { "message", "请求格式错误" }
                });
                        continue;
                    }

                    var reqType = request["type"]?.ToString();
                    Dictionary<string, object> response = null;

                    // 处理注册请求
                    if (reqType == "user_register")
                    {
                        isRegistering = true; // 标记进入注册流程
                        response = _userRegister.Register(request, clientSock);
                        SendResponse(clientSock, response);
                        // 检查是否注册完成（register_3 且 status 为 success）
                        if (response.ContainsKey("subtype") && response["subtype"]?.ToString() == "register_3" &&
                            response.ContainsKey("status") && response["status"]?.ToString() == "success")
                        {
                            isRegistering = false;
                        }
                    }
                    // 处理认证请求
                    else if (reqType == "authenticate")
                    {
                        response = Authenticate(request, clientSock);
                        if (response["status"].ToString() == "success")
                        {
                            loggedInUser = request["username"].ToString();
                            // 发送认证响应
                            SendResponse(clientSock, response);
                            // 认证成功后推送好友列表和更新
                            PushFriendsList(loggedInUser);
                            PushFriendsUpdate(loggedInUser);
                        }
                        else
                        {
                            SendResponse(clientSock, response);
                        }
                    }
                    // 处理退出请求
                    else if (reqType == "exit")
                    {
                        Exit(request, clientSock, loggedInUser);
                        break; // 客户端主动请求退出，断开连接
                    }
                    // 注册流程中只允许 user_register 请求
                    else if (isRegistering)
                    {
                        response = new Dictionary<string, object>
                        {
                            { "type", reqType },
                            { "status", "error" },
                            { "message", "注册流程中仅允许 user_register 请求" },
                            { "request_id", request["request_id"]?.ToString() }
                        };
                        _logger.LogWarning($"注册流程中收到非法请求: type={reqType}, client={clientAddr.ip}:{clientAddr.port}");
                        SendResponse(clientSock, response);
                    }
                    // 未登录且不在注册流程中
                    else if (loggedInUser == null)
                    {
                        response = new Dictionary<string, object>
                        {
                            { "type", reqType },
                            { "status", "error" },
                            { "message", "请先登录或注册" },
                            { "request_id", request["request_id"]?.ToString() }
                        };
                        _logger.LogWarning($"未登录客户端 {clientAddr.ip}:{clientAddr.port} 尝试发送请求: {reqType}");
                        SendResponse(clientSock, response);
                    }
                    // 已登录，处理其他请求
                    else
                    {
                        switch (reqType)
                        {
                            case "send_message":
                                response = SendMessage(request, clientSock);
                                break;
                            case "get_user_info":
                                response = GetUserInfo(request, clientSock);
                                break;
                            case "send_media":
                                response = SendMedia(request, clientSock);
                                break;
                            case "upload_avatar":
                            case "update_sign":
                            case "update_name":
                                response = UpdateUserProfile(request, clientSock);
                                break;
                            case "download_media":
                                try
                                {
                                    DownloadMedia(request, clientSock);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError($"处理 download_media 请求失败: error={ex.Message}, request={JsonConvert.SerializeObject(request)}");
                                    SendResponse(clientSock, new Dictionary<string, object>
                            {
                                { "type", "download_media" },
                                { "status", "error" },
                                { "message", $"下载请求失败: {ex.Message}" },
                                { "request_id", request["request_id"]?.ToString() }
                            });
                                }
                                continue;
                            case "get_chat_history_paginated":
                                try
                                {
                                    GetChatHistoryPaginated(request, clientSock);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError($"处理 get_chat_history_paginated 请求失败: error={ex.Message}, request={JsonConvert.SerializeObject(request)}");
                                    SendResponse(clientSock, new Dictionary<string, object>
                            {
                                { "type", "get_chat_history_paginated" },
                                { "status", "error" },
                                { "message", $"请求失败: {ex.Message}" },
                                { "request_id", request["request_id"]?.ToString() }
                            });
                                }
                                continue;
                            case "add_friend":
                                response = AddFriend(request, clientSock);
                                break;
                            case "Update_Remarks":
                                response = UpdateFriendRemarks(request, clientSock);
                                break;
                            case "delete_messages":
                                response = DeleteMessages(request, clientSock);
                                break;
                            default:
                                response = new Dictionary<string, object>
                        {
                            { "type", reqType },
                            { "status", "error" },
                            { "message", "未知的请求类型" },
                            { "request_id", request["request_id"]?.ToString() }
                        };
                                break;
                        }
                        if (response != null)
                            SendResponse(clientSock, response);
                    }
                }
            }
            catch (SocketException ex)
            {
                _logger.LogWarning($"客户端 {clientAddr.ip}:{clientAddr.port} 强制断开连接: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"处理客户端请求时出现异常：{ex.Message}");
            }
            finally
            {
                if (loggedInUser != null)
                {
                    lock (_clientsLock)
                    {
                        if (_clients.TryGetValue(loggedInUser, out var sock) && sock == clientSock)
                        {
                            _clients.TryRemove(loggedInUser, out _);
                            PushFriendsUpdate(loggedInUser);
                        }
                    }
                    _logger.LogInformation($"用户 {loggedInUser} 已退出");
                }
                else
                {
                    lock (_clientsLock)
                    {
                        var userToRemove = _clients.FirstOrDefault(kvp => kvp.Value == clientSock).Key;
                        if (userToRemove != null)
                        {
                            _clients.TryRemove(userToRemove, out _);
                            _logger.LogInformation($"用户 {userToRemove} 已退出");
                            PushFriendsUpdate(userToRemove);
                        }
                    }
                }
                clientSock.Close();
                _logger.LogInformation($"关闭连接：{clientAddr.ip}:{clientAddr.port}");
            }
        }

        private void Exit(Dictionary<string, object> request, Socket clientSock, string loggedInUser)
        {
            var response = new Dictionary<string, object>
            {
                { "type", "exit" },
                { "status", "success" },
                { "message", $"{loggedInUser} 已退出" },
                { "request_id", request["request_id"]?.ToString() }
            };
            SendResponse(clientSock, response);
            _logger.LogInformation($"用户 {loggedInUser} 请求退出");
        }

        private Dictionary<string, object> GetUserInfo(Dictionary<string, object> request, Socket clientSock)
        {
            var username = request["username"]?.ToString();
            var requestId = request["request_id"]?.ToString();

            using var conn = GetDbConnection();
            if (conn == null)
                return new Dictionary<string, object> { { "type", "get_user_info" }, { "status", "error" }, { "message", "数据库连接失败" }, { "request_id", requestId } };

            try
            {
                using var cmd = new MySqlCommand("SELECT username, avatars, names, signs FROM users WHERE username = @username", conn);
                cmd.Parameters.AddWithValue("@username", username);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    var avatarId = reader.IsDBNull(reader.GetOrdinal("avatars")) ? "" : reader.GetString("avatars");
                    var response = new Dictionary<string, object>
                    {
                        { "type", "get_user_info" },
                        { "status", "success" },
                        { "username", reader.GetString("username") },
                        { "avatar_id", avatarId },
                        { "name", reader.IsDBNull(reader.GetOrdinal("names")) ? reader.GetString("username") : reader.GetString("names") },
                        { "sign", reader.IsDBNull(reader.GetOrdinal("signs")) ? "" : reader.GetString("signs") },
                        { "request_id", requestId }
                    };
                    _logger.LogDebug($"发送用户信息响应：{JsonConvert.SerializeObject(response)}");
                    SendResponse(clientSock, response);
                    return response;
                }
                else
                {
                    var response = new Dictionary<string, object> { { "type", "get_user_info" }, { "status", "error" }, { "message", "用户不存在" }, { "request_id", requestId } };
                    SendResponse(clientSock, response);
                    return response;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"查询用户信息失败: {ex.Message}");
                var response = new Dictionary<string, object> { { "type", "get_user_info" }, { "status", "error" }, { "message", "查询失败" }, { "request_id", requestId } };
                SendResponse(clientSock, response);
                return response;
            }
        }

        private void DownloadMedia(Dictionary<string, object> request, Socket clientSock)
        {
            string requestId = request.ContainsKey("request_id") ? request["request_id"]?.ToString() : null;
            var delayInterval = TimeSpan.FromMilliseconds(0); // 模拟延迟：1.5秒

            try
            {
                // —— Step 1. 安全解析 single_request —— 
                request.TryGetValue("single_request", out object singleReqObj);
                bool isSingleRequest = false;
                if (singleReqObj != null && bool.TryParse(singleReqObj.ToString(), out bool tmp))
                    isSingleRequest = tmp;
                _logger.LogDebug($"解析 single_request: value={(singleReqObj ?? "null")}, isSingleRequest={isSingleRequest}");

                // —— Step 2. 验证 download_type —— 
                string downloadType = request.ContainsKey("download_type") ? request["download_type"]?.ToString() : null;
                if (string.IsNullOrEmpty(downloadType) ||
                    !new HashSet<string> { "avatar", "image", "video", "file", "thumbnail" }.Contains(downloadType))
                {
                    _logger.LogError($"无效的 download_type: {downloadType}");
                    Thread.Sleep(delayInterval);
                    SendResponse(clientSock, new Dictionary<string, object>
            {
                { "type", "download_media" },
                { "status", "error" },
                { "message", "无效的 download_type" },
                { "request_id", requestId }
            });
                    return;
                }

                // —— Step 3. 解析 file_ids / file_id —— 
                List<string> fileIds = new();
                request.TryGetValue("file_ids", out object fidsObj);
                if (fidsObj != null)
                {
                    fileIds = (fidsObj as JArray)?
                        .Select(j => j.ToString()).ToList()
                        ?? JsonConvert.DeserializeObject<List<string>>(JsonConvert.SerializeObject(fidsObj));
                    _logger.LogDebug($"解析到 file_ids: {string.Join(", ", fileIds)}");
                }
                else if (request.TryGetValue("file_id", out object fidObj) && fidObj != null)
                {
                    fileIds.Add(fidObj.ToString());
                    _logger.LogDebug($"解析到 single file_id: {fidObj}");
                }

                if (!fileIds.Any())
                {
                    _logger.LogError("未提供有效的文件ID");
                    Thread.Sleep(delayInterval);
                    SendResponse(clientSock, new Dictionary<string, object>
            {
                { "type", "download_media" },
                { "status", "error" },
                { "message", "未提供文件ID" },
                { "request_id", requestId }
            });
                    return;
                }

                // —— Step 4. 查询数据库拿到路径、大小、MD5 —— 
                var filePaths = new Dictionary<string, string>();
                var fileSizes = new Dictionary<string, long>();
                var fileChecksums = new Dictionary<string, string>();
                using var conn = GetDbConnection();
                if (conn == null)
                {
                    _logger.LogError("数据库连接失败");
                    Thread.Sleep(delayInterval);
                    SendResponse(clientSock, new Dictionary<string, object>
            {
                { "type", "download_media" },
                { "status", "error" },
                { "message", "数据库连接失败" },
                { "request_id", requestId }
            });
                    return;
                }

                foreach (var fileId in fileIds)
                {
                    try
                    {
                        using var cmd = conn.CreateCommand();
                        if (downloadType == "avatar")
                        {
                            cmd.CommandText = "SELECT avatar_path FROM users WHERE avatars = @fileId";
                            cmd.Parameters.AddWithValue("@fileId", fileId);
                        }
                        else
                        {
                            cmd.CommandText = "SELECT thumbnail_path FROM messages WHERE file_id = @fileId";
                            cmd.Parameters.AddWithValue("@fileId", fileId);
                        }

                        using var reader = cmd.ExecuteReader();
                        if (!reader.Read() || reader.IsDBNull(0))
                            throw new FileNotFoundException($"数据库中无此文件记录: {fileId}");

                        var rawPath = reader.GetString(0);
                        reader.Close();

                        var fullPath = Path.IsPathRooted(rawPath)
                            ? rawPath
                            : Path.Combine(Directory.GetCurrentDirectory(), rawPath);
                        if (!File.Exists(fullPath))
                            throw new FileNotFoundException($"磁盘上文件不存在: {fullPath}");

                        filePaths[fileId] = fullPath;
                        fileSizes[fileId] = new FileInfo(fullPath).Length;
                        fileChecksums[fileId] = ComputeMD5(fullPath);
                        _logger.LogDebug($"文件查询成功: {fileId}, path={fullPath}, size={fileSizes[fileId]}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"查询文件失败: {fileId}, 错误: {ex.Message}");
                        Thread.Sleep(delayInterval);
                        SendResponse(clientSock, new Dictionary<string, object>
                {
                    { "type", "download_media" },
                    { "status", "error" },
                    { "message", ex.Message },
                    { "file_id", fileId },
                    { "request_id", requestId }
                });
                    }
                }

                // —— Step 5. 初始化元数据，仅批量下载且非一次性请求时发送 —— 
                if (request.ContainsKey("file_ids") && !isSingleRequest)
                {
                    var initResp = new Dictionary<string, object>
            {
                { "type", "download_media" },
                { "status", "success" },
                { "message", "下载初始化" },
                { "request_id", requestId },
                { "file_sizes", fileSizes },
                { "file_checksums", fileChecksums }
            };
                    Thread.Sleep(delayInterval);
                    SendResponse(clientSock, initResp);
                    _logger.LogDebug($"已发送初始化响应: files=[{string.Join(", ", filePaths.Keys)}]");
                    return;
                }

                // —— Step 6. 一次性下载 —— 
                if (isSingleRequest)
                {
                    foreach (var kv in filePaths)
                    {
                        var id = kv.Key;
                        var path = kv.Value;
                        var data = File.ReadAllBytes(path);
                        Thread.Sleep(delayInterval);
                        SendResponse(clientSock, new Dictionary<string, object>
                {
                    { "type", "download_media" },
                    { "status", "success" },
                    { "file_id", id },
                    { "file_size", fileSizes[id] },
                    { "file_data", Convert.ToBase64String(data) },
                    { "is_complete", true },
                    { "request_id", requestId }
                });
                        _logger.LogDebug($"一次性发送文件: {id}, size={data.Length}");
                    }
                    return;
                }

                // —— Step 7. 分块传输 —— 
                if (!request.TryGetValue("offset", out object offObj) ||
                    !long.TryParse(offObj?.ToString(), out long offset))
                {
                    _logger.LogError("分块请求缺少或无效 offset");
                    Thread.Sleep(delayInterval);
                    SendResponse(clientSock, new Dictionary<string, object>
            {
                { "type", "download_media" },
                { "status", "error" },
                { "message", "分块请求缺少或无效 offset" },
                { "request_id", requestId }
            });
                    return;
                }

                var singleFileId = fileIds.Count == 1 ? fileIds[0] : null;
                if (singleFileId == null)
                {
                    _logger.LogError("分块下载只支持单文件");
                    Thread.Sleep(delayInterval);
                    SendResponse(clientSock, new Dictionary<string, object>
            {
                { "type", "download_media" },
                { "status", "error" },
                { "message", "分块下载只支持单文件" },
                { "request_id", requestId }
            });
                    return;
                }

                var filePathSingle = filePaths[singleFileId];
                var totalSize = fileSizes[singleFileId];
                const int chunkSize = 1024 * 1024;
                using var fs = new FileStream(filePathSingle, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Seek(offset, SeekOrigin.Begin);
                var buffer = new byte[chunkSize];
                int read = fs.Read(buffer, 0, chunkSize);
                bool done = offset + read >= totalSize;

                Thread.Sleep(delayInterval);
                SendResponse(clientSock, new Dictionary<string, object>
        {
            { "type", "download_media" },
            { "status", "success" },
            { "file_id", singleFileId },
            { "file_size", totalSize },
            { "offset", offset },
            { "file_data", read > 0 ? Convert.ToBase64String(buffer, 0, read) : string.Empty },
            { "is_complete", done },
            { "request_id", requestId }
        });
                _logger.LogDebug($"分块发送: file_id={singleFileId}, offset={offset}, bytes={read}, is_complete={done}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"处理 DownloadMedia 失败: {ex.Message}");
                Thread.Sleep(delayInterval);
                SendResponse(clientSock, new Dictionary<string, object>
        {
            { "type", "download_media" },
            { "status", "error" },
            { "message", $"下载请求失败: {ex.Message}" },
            { "request_id", requestId }
        });
            }
        }



        private string ComputeMD5(string filePath)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hash = md5.ComputeHash(stream);
            return Convert.ToBase64String(hash);
        }
    }
}
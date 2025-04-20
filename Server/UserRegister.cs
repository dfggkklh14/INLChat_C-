using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Server
{
    public class UserRegister
    {
        private readonly ILogger<UserRegister> _logger;
        private readonly ConcurrentDictionary<string, CaptchaSession> _captchaSessions = new ConcurrentDictionary<string, CaptchaSession>();
        private readonly object _captchaLock = new object();
        private readonly string _baseDir = "user_data";
        private const int TTL = 300; // 验证码和会话有效期5分钟

        private class CaptchaSession
        {
            public string Username { get; set; }
            public string Captcha { get; set; }
            public double CreatedAt { get; set; }
            public bool Verified { get; set; }
        }

        public UserRegister(ILogger<UserRegister> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private MySqlConnection GetDbConnection()
        {
            try
            {
                // 与主文件保持一致的数据库连接配置
                var connectionString = "Server=localhost;User ID=root;Password=Aa112211;Database=chat_server;Charset=utf8mb4;";
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

        private string GenerateUsername()
        {
            using var conn = GetDbConnection();
            if (conn == null)
            {
                _logger.LogError("无法连接数据库，无法生成用户名");
                return null;
            }

            try
            {
                using var cmd = conn.CreateCommand();
                while (true)
                {
                    var length = RandomNumberGenerator.GetInt32(8, 11); // 8-10位
                    var firstDigit = "123456789"[RandomNumberGenerator.GetInt32(9)]; // 首位不为0
                    var otherDigits = new char[length - 1];
                    for (int i = 0; i < otherDigits.Length; i++)
                        otherDigits[i] = "0123456789"[RandomNumberGenerator.GetInt32(10)];
                    var username = firstDigit + new string(otherDigits);

                    // 检查数据库中是否已存在该 username，SQL 查询与 Python 一致
                    cmd.CommandText = "SELECT username FROM users WHERE username = @username";
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("@username", username);
                    using var reader = cmd.ExecuteReader();
                    if (!reader.Read()) // 没有找到重复的 username
                    {
                        reader.Close();
                        return username;
                    }
                    reader.Close();
                    _logger.LogDebug($"用户名 {username} 已存在，重新生成");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"数据库查询失败: {ex.Message}");
                return null;
            }
        }

        private (string CaptchaText, string CaptchaImageBase64) GenerateCaptchaImage()
        {
            // 生成6位验证码，包含大写字母和数字
            var chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var captchaText = new char[6];
            for (int i = 0; i < 6; i++)
                captchaText[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
            var captchaString = new string(captchaText);

            // 使用 SixLabors.ImageSharp 和 SixLabors.Fonts 生成验证码图片
            using var image = new Image<Rgba32>(200, 80); // 宽200，高80
            image.Mutate(ctx =>
            {
                ctx.BackgroundColor(Color.White);

                // 加载字体
                var fontCollection = new FontCollection();
                var font = fontCollection.Add("C:\\Windows\\Fonts\\arial.ttf").CreateFont(36); // Windows 环境
                var rendererOptions = new RichTextOptions(font)
                {
                    Origin = new PointF(20, 50)
                    // 移除 WrappingWidth，验证码为单行文本，无需换行
                };

                // 绘制验证码文字
                ctx.DrawText(rendererOptions, captchaString, Color.Black);

                // 添加干扰线
                for (int i = 0; i < 5; i++)
                {
                    var start = new PointF(RandomNumberGenerator.GetInt32(200), RandomNumberGenerator.GetInt32(80));
                    var end = new PointF(RandomNumberGenerator.GetInt32(200), RandomNumberGenerator.GetInt32(80));
                    ctx.DrawLine(Color.Gray, 1, start, end);
                }
            });

            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            var imgBase64 = Convert.ToBase64String(ms.ToArray());
            return (captchaString, imgBase64);
        }

        public Dictionary<string, object> Register(Dictionary<string, object> request, Socket clientSock)
        {
            var subtype = request["subtype"]?.ToString();
            var requestId = request["request_id"]?.ToString();
            var sessionId = request.ContainsKey("session_id")
                ? request["session_id"]?.ToString()
                : RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

            // 清理过期会话
            lock (_captchaLock)
            {
                var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var expired = _captchaSessions
                    .Where(kvp => currentTime - kvp.Value.CreatedAt > TTL)
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var sid in expired)
                    _captchaSessions.TryRemove(sid, out _);
            }

            if (subtype == "register_1")
            {
                // 第一次请求：生成用户名和验证码
                var username = GenerateUsername();
                if (username == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "type", "user_register" },
                        { "subtype", subtype },
                        { "status", "error" },
                        { "message", "生成用户名失败" },
                        { "request_id", requestId }
                    };
                }

                var (captchaText, captchaImg) = GenerateCaptchaImage();
                lock (_captchaLock)
                {
                    _captchaSessions[sessionId] = new CaptchaSession
                    {
                        Username = username,
                        Captcha = captchaText,
                        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Verified = false
                    };
                }

                return new Dictionary<string, object>
                {
                    { "type", "user_register" },
                    { "subtype", subtype },
                    { "status", "success" },
                    { "username", username },
                    { "captcha_image", captchaImg },
                    { "session_id", sessionId },
                    { "request_id", requestId }
                };
            }
            else if (subtype == "register_2")
            {
                // 验证验证码
                var userCaptcha = request["captcha_input"]?.ToString();

                if (string.IsNullOrEmpty(userCaptcha))
                {
                    return new Dictionary<string, object>
                    {
                        { "type", "user_register" },
                        { "subtype", subtype },
                        { "status", "error" },
                        { "message", "验证码输入缺失" },
                        { "request_id", requestId }
                    };
                }

                lock (_captchaLock)
                {
                    if (!_captchaSessions.TryGetValue(sessionId, out var session))
                    {
                        return new Dictionary<string, object>
                        {
                            { "type", "user_register" },
                            { "subtype", subtype },
                            { "status", "error" },
                            { "message", "会话无效" },
                            { "request_id", requestId }
                        };
                    }

                    var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (currentTime - session.CreatedAt > TTL)
                    {
                        _captchaSessions.TryRemove(sessionId, out _);
                        return new Dictionary<string, object>
                        {
                            { "type", "user_register" },
                            { "subtype", subtype },
                            { "status", "error" },
                            { "message", "会话已过期" },
                            { "request_id", requestId }
                        };
                    }

                    if (userCaptcha.ToUpper() != session.Captcha)
                    {
                        // 验证失败，重新生成验证码
                        var (newCaptchaText, newCaptchaImg) = GenerateCaptchaImage();
                        session.Captcha = newCaptchaText;
                        session.CreatedAt = currentTime; // 重置时间
                        return new Dictionary<string, object>
                        {
                            { "type", "user_register" },
                            { "subtype", subtype },
                            { "status", "fail" },
                            { "message", "验证码错误" },
                            { "captcha_image", newCaptchaImg },
                            { "session_id", sessionId },
                            { "request_id", requestId }
                        };
                    }

                    // 验证成功
                    session.Verified = true;
                    return new Dictionary<string, object>
                    {
                        { "type", "user_register" },
                        { "subtype", subtype },
                        { "status", "success" },
                        { "message", "验证码验证成功" },
                        { "username", session.Username },
                        { "session_id", sessionId },
                        { "request_id", requestId }
                    };
                }
            }
            else if (subtype == "register_3")
            {
                // 提交用户信息
                var password = request["password"]?.ToString();
                var avatarData = request.ContainsKey("avatar_data") ? request["avatar_data"]?.ToString() : "";
                var nickname = request.ContainsKey("nickname") ? request["nickname"]?.ToString() : "";
                var sign = request.ContainsKey("sign") ? request["sign"]?.ToString() : "";

                if (string.IsNullOrEmpty(password) || password.Length < 8 || !password.Any(char.IsUpper) || !password.Any(char.IsDigit))
                {
                    return new Dictionary<string, object>
                    {
                        { "type", "user_register" },
                        { "subtype", subtype },
                        { "status", "error" },
                        { "message", "密码必须至少8位，包含大写字母和数字" },
                        { "request_id", requestId }
                    };
                }

                lock (_captchaLock)
                {
                    if (!_captchaSessions.TryGetValue(sessionId, out var session) || !session.Verified)
                    {
                        return new Dictionary<string, object>
                        {
                            { "type", "user_register" },
                            { "subtype", subtype },
                            { "status", "error" },
                            { "message", "会话无效或未验证" },
                            { "request_id", requestId }
                        };
                    }

                    var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (currentTime - session.CreatedAt > TTL)
                    {
                        _captchaSessions.TryRemove(sessionId, out _);
                        return new Dictionary<string, object>
                        {
                            { "type", "user_register" },
                            { "subtype", subtype },
                            { "status", "error" },
                            { "message", "会话已过期" },
                            { "request_id", requestId }
                        };
                    }

                    var username = session.Username;

                    // 使用明文密码，与 Python 代码一致
                    var plainPassword = password;

                    // 头像处理，限制大小为2MB
                    string avatarPath = "";
                    string avatarId = "";
                    if (!string.IsNullOrEmpty(avatarData))
                    {
                        try
                        {
                            var fileData = Convert.FromBase64String(avatarData);
                            if (fileData.Length > 2 * 1024 * 1024) // 2MB限制
                            {
                                return new Dictionary<string, object>
                                {
                                    { "type", "user_register" },
                                    { "subtype", subtype },
                                    { "status", "error" },
                                    { "message", "头像文件不得超过2MB" },
                                    { "request_id", requestId }
                                };
                            }

                            var avatarDir = Path.Combine(_baseDir, "avatars");
                            Directory.CreateDirectory(avatarDir);
                            avatarId = $"{username}_avatar_{DateTime.Now:yyyyMMddHHmmssfffffff}.jpg";
                            avatarPath = Path.Combine(avatarDir, avatarId);
                            File.WriteAllBytes(avatarPath, fileData);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"头像保存失败: {ex.Message}");
                            avatarPath = "";
                            avatarId = "";
                        }
                    }

                    using var conn = GetDbConnection();
                    if (conn == null)
                    {
                        return new Dictionary<string, object>
                        {
                            { "type", "user_register" },
                            { "subtype", subtype },
                            { "status", "error" },
                            { "message", "数据库连接失败" },
                            { "request_id", requestId }
                        };
                    }

                    try
                    {
                        using var cmd = new MySqlCommand(
                            @"INSERT INTO users (username, password, avatars, avatar_path, names, signs)
                              VALUES (@username, @password, @avatars, @avatar_path, @names, @signs)", conn);
                        cmd.Parameters.AddWithValue("@username", username);
                        cmd.Parameters.AddWithValue("@password", plainPassword);
                        cmd.Parameters.AddWithValue("@avatars", avatarId);
                        cmd.Parameters.AddWithValue("@avatar_path", avatarPath);
                        cmd.Parameters.AddWithValue("@names", nickname);
                        cmd.Parameters.AddWithValue("@signs", sign);
                        cmd.ExecuteNonQuery();

                        lock (_captchaLock)
                        {
                            _captchaSessions.TryRemove(sessionId, out _); // 清理会话
                        }

                        return new Dictionary<string, object>
                        {
                            { "type", "user_register" },
                            { "subtype", subtype },
                            { "status", "success" },
                            { "message", "注册成功" },
                            { "username", username },
                            { "request_id", requestId }
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"注册失败: {ex.Message}");
                        return new Dictionary<string, object>
                        {
                            { "type", "user_register" },
                            { "subtype", subtype },
                            { "status", "error" },
                            { "message", "注册失败" },
                            { "request_id", requestId }
                        };
                    }
                }
            }
            else if (subtype == "register_4")
            {
                // 重新生成验证码
                lock (_captchaLock)
                {
                    if (!_captchaSessions.TryGetValue(sessionId, out var session))
                    {
                        return new Dictionary<string, object>
                        {
                            { "type", "user_register" },
                            { "subtype", subtype },
                            { "status", "error" },
                            { "message", "会话无效" },
                            { "request_id", requestId }
                        };
                    }

                    var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    if (currentTime - session.CreatedAt > TTL)
                    {
                        _captchaSessions.TryRemove(sessionId, out _);
                        return new Dictionary<string, object>
                        {
                            { "type", "user_register" },
                            { "subtype", subtype },
                            { "status", "error" },
                            { "message", "会话已过期" },
                            { "request_id", requestId }
                        };
                    }

                    var (captchaText, captchaImg) = GenerateCaptchaImage();
                    session.Captcha = captchaText;
                    session.CreatedAt = currentTime; // 重置时间

                    return new Dictionary<string, object>
                    {
                        { "type", "user_register" },
                        { "subtype", subtype },
                        { "status", "success" },
                        { "captcha_image", captchaImg },
                        { "session_id", sessionId },
                        { "request_id", requestId }
                    };
                }
            }

            return new Dictionary<string, object>
            {
                { "type", "user_register" },
                { "subtype", subtype },
                { "status", "error" },
                { "message", "未知的子类型" },
                { "request_id", requestId }
            };
        }
    }
}
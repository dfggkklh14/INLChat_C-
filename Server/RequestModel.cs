using Newtonsoft.Json;

namespace Server
{
    public class RequestModel
    {
        // 专用于 get_chat_history_paginated 请求的数据模型
        public class ChatHistory
        {
            [JsonProperty("type")]
            public string Type { get; set; }

            [JsonProperty("username")]
            public string Username { get; set; }

            [JsonProperty("friend")]
            public string Friend { get; set; }

            [JsonProperty("page")]
            public int Page { get; set; } = 1;

            [JsonProperty("page_size")]
            public int PageSize { get; set; } = 20;

            [JsonProperty("request_id")]
            public string RequestId { get; set; }
        }

        // 占位符，未来可添加其他类型的嵌套类
        public class SendMessage
        {
            [JsonProperty("type")]
            public string Type { get; set; }

            [JsonProperty("username")]
            public string Username { get; set; }

            [JsonProperty("receiver")]
            public string Receiver { get; set; }

            [JsonProperty("message")]
            public string Message { get; set; }

            [JsonProperty("request_id")]
            public string RequestId { get; set; }
        }

        public class GetFriends
        {
            [JsonProperty("type")]
            public string Type { get; set; }

            [JsonProperty("username")]
            public string Username { get; set; }

            [JsonProperty("request_id")]
            public string RequestId { get; set; }
        }
    }


    public class ServerConfig
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public DbConfig DbConfig { get; set; }
        public LoggingConfig Logging { get; set; }
    }

    public class DbConfig
    {
        public string Host { get; set; }
        public string User { get; set; }
        public string Password { get; set; }
        public string Database { get; set; }
        public string Charset { get; set; }
        public string Collation { get; set; }
    }

    public class LoggingConfig
    {
        public string Level { get; set; }
        public string Format { get; set; }
    }

    public class UploadSession
    {
        public string FilePath { get; set; }
        public long TotalSize { get; set; }
        public long ReceivedSize { get; set; }
        public string UniqueFileName { get; set; }
    }

    public class MessageData
    {
        public long RowId { get; set; }
        public string Sender { get; set; }
        public string Receiver { get; set; }
        public string Message { get; set; }
        public string WriteTime { get; set; }
        public string AttachmentType { get; set; }
        public string OriginalFileName { get; set; }
        public long? ReplyTo { get; set; }
        public string ReplyPreview { get; set; }
        public string FileId { get; set; }
    }
}
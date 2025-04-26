using System.Text.Json.Serialization;

namespace Server
{
    public class RequestModel
    {
        // 专用于 get_chat_history_paginated 请求的数据模型
        public class ChatHistory
        {
            [JsonPropertyName("type")]
            public string Type { get; set; }

            [JsonPropertyName("username")]
            public string Username { get; set; }

            [JsonPropertyName("friend")]
            public string Friend { get; set; }

            [JsonPropertyName("page")]
            public int Page { get; set; } = 1;

            [JsonPropertyName("page_size")]
            public int PageSize { get; set; } = 20;

            [JsonPropertyName("request_id")]
            public string RequestId { get; set; }
        }

        // 占位符，未来可添加其他类型的嵌套类
        public class SendMessage
        {
            [JsonPropertyName("type")]
            public string Type { get; set; }

            [JsonPropertyName("username")]
            public string Username { get; set; }

            [JsonPropertyName("receiver")]
            public string Receiver { get; set; }

            [JsonPropertyName("message")]
            public string Message { get; set; }

            [JsonPropertyName("request_id")]
            public string RequestId { get; set; }
        }

        public class GetFriends
        {
            [JsonPropertyName("type")]
            public string Type { get; set; }

            [JsonPropertyName("username")]
            public string Username { get; set; }

            [JsonPropertyName("request_id")]
            public string RequestId { get; set; }
        }
    }
}
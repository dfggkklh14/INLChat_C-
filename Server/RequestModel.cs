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
}
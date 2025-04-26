using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Client.Function
{
    public class FriendModel : INotifyPropertyChanged
    {
        private string _username;
        private string _avatarId;
        private bool _online;
        private string _name;
        private string _sign;
        private ConversationModel _conversation;
        private int _unreadCount;

        public string Username
        {
            get => _username;
            set { _username = value; OnPropertyChanged(); }
        }
        public string AvatarId
        {
            get => _avatarId;
            set { _avatarId = value; OnPropertyChanged(); }
        }
        public bool Online
        {
            get => _online;
            set { _online = value; OnPropertyChanged(); }
        }
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }
        public string Sign
        {
            get => _sign;
            set { _sign = value; OnPropertyChanged(); }
        }
        public ConversationModel Conversation
        {
            get => _conversation;
            set { _conversation = value; OnPropertyChanged(); }
        }
        public int UnreadCount
        {
            get => _unreadCount;
            set { _unreadCount = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public class ConversationModel : INotifyPropertyChanged
        {
            private string _sender;
            private string _content;
            private string _lastUpdateTime;

            public event PropertyChangedEventHandler PropertyChanged;

            public string Sender
            {
                get => _sender;
                set { _sender = value; OnPropertyChanged(); }
            }

            public string Content
            {
                get => _content;
                set { _content = value; OnPropertyChanged(); }
            }

            public string LastUpdateTime
            {
                get => _lastUpdateTime;
                set
                {
                    if (DateTime.TryParse(value, out var dateTime))
                    {
                        _lastUpdateTime = dateTime.ToString("HH:mm");
                    }
                    else
                    {
                        _lastUpdateTime = "00:00";
                    }
                    OnPropertyChanged();
                }
            }

            protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        public static FriendModel FromDictionary(Dictionary<string, object> friendData, Dictionary<string, int> unreadMessages)
        {
            var model = new FriendModel
            {
                Username = friendData.GetValueOrDefault("username")?.ToString(),
                AvatarId = friendData.GetValueOrDefault("avatar_id")?.ToString(),
                Name = friendData.GetValueOrDefault("name")?.ToString(),
                Sign = friendData.GetValueOrDefault("sign")?.ToString(),
                Online = Convert.ToBoolean(friendData.GetValueOrDefault("online") ?? false),
                UnreadCount = unreadMessages.GetValueOrDefault(friendData.GetValueOrDefault("username")?.ToString(), 0)
            };

            if (friendData.ContainsKey("conversations") && friendData["conversations"] is Dictionary<string, object> conv)
            {
                model.Conversation = new ConversationModel
                {
                    Sender = conv.GetValueOrDefault("sender")?.ToString(),
                    Content = conv.GetValueOrDefault("content")?.ToString(),
                    LastUpdateTime = conv.GetValueOrDefault("last_update_time")?.ToString()
                };
            }

            return model;
        }
    }
}
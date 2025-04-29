using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Client.Function
{
    public class ChatHistoryEntry : INotifyPropertyChanged
    {
        private int _rowid;
        private string _writeTime;
        private string _senderUsername;
        private string _message;
        private bool _isCurrentUser;
        private int? _replyTo;
        private string _replyPreview;
        private string _attachmentType;
        private string _fileId;
        private string _originalFileName;
        private string _thumbnailPath;
        private string _thumbnailLocalPath;
        private long? _fileSize;
        private double? _duration;

        public int Rowid
        {
            get => _rowid;
            set
            {
                _rowid = value;
                OnPropertyChanged();
            }
        }

        public string WriteTime
        {
            get => _writeTime;
            set
            {
                _writeTime = value;
                OnPropertyChanged();
            }
        }

        public string SenderUsername
        {
            get => _senderUsername;
            set
            {
                _senderUsername = value;
                OnPropertyChanged();
            }
        }

        public string Message
        {
            get => _message;
            set
            {
                _message = value;
                OnPropertyChanged();
            }
        }

        public bool IsCurrentUser
        {
            get => _isCurrentUser;
            set
            {
                _isCurrentUser = value;
                OnPropertyChanged();
            }
        }

        public int? ReplyTo
        {
            get => _replyTo;
            set
            {
                _replyTo = value;
                OnPropertyChanged();
            }
        }

        public string ReplyPreview
        {
            get => _replyPreview;
            set
            {
                _replyPreview = value;
                OnPropertyChanged();
            }
        }

        public string AttachmentType
        {
            get => _attachmentType;
            set
            {
                _attachmentType = value;
                OnPropertyChanged();
            }
        }

        public string FileId
        {
            get => _fileId;
            set
            {
                _fileId = value;
                OnPropertyChanged();
            }
        }

        public string OriginalFileName
        {
            get => _originalFileName;
            set
            {
                _originalFileName = value;
                OnPropertyChanged();
            }
        }

        public string ThumbnailPath
        {
            get => _thumbnailPath;
            set
            {
                _thumbnailPath = value;
                OnPropertyChanged();
            }
        }

        public string ThumbnailLocalPath
        {
            get => _thumbnailLocalPath;
            set
            {
                _thumbnailLocalPath = value;
                OnPropertyChanged();
            }
        }

        public long? FileSize
        {
            get => _fileSize;
            set
            {
                _fileSize = value;
                OnPropertyChanged();
            }
        }

        public double? Duration
        {
            get => _duration;
            set
            {
                _duration = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
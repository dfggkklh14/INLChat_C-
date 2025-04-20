using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Client.Utility
{
    public partial class FriendItemControl : UserControl
    {
        // 依赖属性
        public static readonly DependencyProperty UsernameProperty = DependencyProperty.Register(
            "Username", typeof(string), typeof(FriendItemControl), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty DisplayNameProperty = DependencyProperty.Register( // 重命名为 DisplayName
            "DisplayName", typeof(string), typeof(FriendItemControl), new PropertyMetadata(string.Empty, OnDisplayNameChanged));
        public static readonly DependencyProperty IsOnlineProperty = DependencyProperty.Register(
            "IsOnline", typeof(bool), typeof(FriendItemControl), new PropertyMetadata(false, OnIsOnlineChanged));
        public static readonly DependencyProperty UnreadProperty = DependencyProperty.Register(
            "Unread", typeof(int), typeof(FriendItemControl), new PropertyMetadata(0));
        public static readonly DependencyProperty AvatarIdProperty = DependencyProperty.Register(
            "AvatarId", typeof(string), typeof(FriendItemControl), new PropertyMetadata(null, OnAvatarIdChanged));
        public static readonly DependencyProperty LastMessageProperty = DependencyProperty.Register(
            "LastMessage", typeof(string), typeof(FriendItemControl), new PropertyMetadata(string.Empty));
        public static readonly DependencyProperty LastMessageTimeProperty = DependencyProperty.Register(
            "LastMessageTime", typeof(string), typeof(FriendItemControl), new PropertyMetadata(string.Empty));

        // 属性
        public string Username
        {
            get => (string)GetValue(UsernameProperty);
            set => SetValue(UsernameProperty, value);
        }

        public string DisplayName // 重命名为 DisplayName
        {
            get => (string)GetValue(DisplayNameProperty);
            set => SetValue(DisplayNameProperty, value);
        }

        public bool IsOnline
        {
            get => (bool)GetValue(IsOnlineProperty);
            set => SetValue(IsOnlineProperty, value);
        }

        public int Unread
        {
            get => (int)GetValue(UnreadProperty);
            set => SetValue(UnreadProperty, value);
        }

        public string AvatarId
        {
            get => (string)GetValue(AvatarIdProperty);
            set => SetValue(AvatarIdProperty, value);
        }

        public string LastMessage
        {
            get => (string)GetValue(LastMessageProperty);
            set => SetValue(LastMessageProperty, value);
        }

        public string LastMessageTime
        {
            get => (string)GetValue(LastMessageTimeProperty);
            set => SetValue(LastMessageTimeProperty, value);
        }

        // 事件
        public event EventHandler<string> FriendClicked;

        private readonly string _cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Chat_DATA", "avatars");

        public FriendItemControl()
        {
            InitializeComponent();
            Directory.CreateDirectory(_cacheDir);
            UpdateAvatar();
        }

        private void OnClick(object sender, MouseButtonEventArgs e)
        {
            if (!string.IsNullOrEmpty(Username))
            {
                FriendClicked?.Invoke(this, Username);
            }
        }

        private static void OnDisplayNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) // 更新回调
        {
            var control = (FriendItemControl)d;
            control.AdjustNameFont();
        }

        private static void OnIsOnlineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (FriendItemControl)d;
            control.InvalidateVisual(); // 触发 UI 更新
        }

        private static void OnAvatarIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (FriendItemControl)d;
            control.UpdateAvatar();
        }

        private void AdjustNameFont()
        {
            if (string.IsNullOrEmpty(DisplayName)) return; // 使用 DisplayName

            double fontSize = 12;
            var formattedText = new FormattedText(
                DisplayName,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                fontSize,
                Brushes.Black,
                96);

            while (formattedText.Width > 110 && fontSize > 6)
            {
                fontSize -= 0.5;
                formattedText.SetFontSize(fontSize);
            }

            NameTextBlock.FontSize = fontSize;
            NameTextBlock.Width = Math.Min(formattedText.Width, 110);
        }

        private void UpdateAvatar()
        {
            if (!string.IsNullOrEmpty(AvatarId))
            {
                string savePath = Path.Combine(_cacheDir, AvatarId);
                if (File.Exists(savePath) && new FileInfo(savePath).Length > 0)
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(savePath, UriKind.Absolute);
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        AvatarImage.Source = bitmap;
                        return;
                    }
                    catch
                    {
                        File.Delete(savePath); // 删除无效缓存
                    }
                }
            }

            // 显示默认头像
            SetDefaultAvatar();
        }

        private void SetDefaultAvatar()
        {
            try
            {
                string defaultAvatarPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon", "default_avatar.ico");
                if (File.Exists(defaultAvatarPath))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(defaultAvatarPath, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    AvatarImage.Source = bitmap;
                }
                else
                {
                    AvatarImage.Source = null;
                }
            }
            catch
            {
                AvatarImage.Source = null;
            }
        }
    }

    // 转换器保持不变，与你的 XAML 一致
    public class OnlineToColorConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            bool isOnline = (bool)value;
            return new SolidColorBrush(isOnline ? Color.FromRgb(53, 252, 141) : Color.FromRgb(211, 211, 211));
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class OnlineToBorderColorConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            bool isOnline = (bool)value;
            return new SolidColorBrush(isOnline ? Color.FromRgb(53, 252, 141) : Color.FromRgb(211, 211, 211));
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class UnreadToVisibilityConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            int unread = (int)value;
            return unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class UnreadToTimeVisibilityConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            int unread = (int)value;
            return unread > 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class TimeConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            string timestamp = value as string;
            if (string.IsNullOrEmpty(timestamp)) return string.Empty;

            try
            {
                DateTime dt = DateTime.ParseExact(timestamp, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                if (DateTime.Now - dt < TimeSpan.FromDays(1))
                {
                    return dt.ToString("HH:mm");
                }
                return dt.ToString("MM.dd HH:mm");
            }
            catch
            {
                return DateTime.Now.ToString("HH:mm");
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
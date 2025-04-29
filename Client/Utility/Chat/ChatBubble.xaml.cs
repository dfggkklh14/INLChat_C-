using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Client.Utility.Chat
{
    public partial class ChatBubble : UserControl
    {
        public ChatBubble()
        {
            InitializeComponent();
        }

        private void ThumbnailImageLoaded(object sender, RoutedEventArgs e)
        {
            var img = sender as Image;
            if (img != null)
            {
                img.Clip = new RectangleGeometry()
                {
                    Rect = new Rect(0, 0, img.ActualWidth, img.ActualHeight),
                    RadiusX = 12,
                    RadiusY = 12
                };
            }
        }
    }

    // 转换器：根据 AttachmentType 判断是否显示特定元素
    public class AttachmentTypeToVisibilityConverter : IValueConverter
    {
        public string TargetType { get; set; }

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var attachmentType = value?.ToString();
            return attachmentType == TargetType ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // 转换器：根据 Message 和 AttachmentType 判断 MessageTextBlock 是否显示
    public class MessageAndAttachmentToVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (values.Length != 2) return Visibility.Collapsed;

            var message = values[0]?.ToString();
            var attachmentType = values[1]?.ToString();

            if (string.IsNullOrEmpty(attachmentType) && !string.IsNullOrEmpty(message))
                return Visibility.Visible;

            if (!string.IsNullOrEmpty(attachmentType) && !string.IsNullOrEmpty(message) &&
                (attachmentType == "file" || attachmentType == "image" || attachmentType == "video"))
                return Visibility.Visible;

            return Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // 转换器：根据 ReplyTo 判断 ReplyContainer 是否显示
    public class ReplyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return value != null ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // 转换器：判断字符串是否为 null 或空
    public class NullOrEmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return string.IsNullOrEmpty(value?.ToString()) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // 转换器：时间戳格式化
    public class TimeStampConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            try
            {
                if (string.IsNullOrEmpty(value?.ToString()))
                    return "00:00";

                if (!DateTime.TryParse(value.ToString(), out DateTime writeTime))
                    return "00:00";

                DateTime now = DateTime.Now;
                TimeSpan timeDiff = now - writeTime;

                if (writeTime.Year != now.Year)
                    return writeTime.ToString("yyyy-M-d HH:mm");

                if (timeDiff.TotalHours <= 24)
                    return writeTime.ToString("HH:mm");
                else
                    return writeTime.ToString("M-d HH:mm");
            }
            catch
            {
                return "00:00";
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }


    // 转换器：IsCurrentUser 到颜色（BubbleBackg 和 BubblePointer）
    public class IsCurrentUserToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            bool isCurrentUser = (bool)value;
            return isCurrentUser ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#aaeb7b"))
                                : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ffffff"));
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // 转换器：IsCurrentUser 到三角形点
    public class IsCurrentUserToPointsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            bool isCurrentUser = (bool)value;
            return isCurrentUser ? new PointCollection(new[] { new Point(0, 0), new Point(8, 6), new Point(0, 12) }) // 右三角
                                : new PointCollection(new[] { new Point(8, 0), new Point(0, 6), new Point(8, 12) }); // 左三角
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // 转换器：IsCurrentUser 到对齐方式或 Grid.Column
    public class IsCurrentUserToAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isCurrentUser = (bool)value;
            if (parameter?.ToString() == "GridColumn")
                return isCurrentUser ? 2 : 0;
            return isCurrentUser
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Left;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class TimestampAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isCurrentUser = (bool)value;
            if (parameter?.ToString() == "GridColumn")
                return isCurrentUser ? 2 : 0;
            return isCurrentUser
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Right;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // 新增：IsCurrentUser 到 ReplyBackgr 颜色
    public class IsCurrentUserToReplyColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            bool isCurrentUser = (bool)value;
            return isCurrentUser ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#90db5a"))
                                : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ededed"));
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}


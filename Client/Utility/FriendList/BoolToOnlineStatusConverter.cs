using System;
using System.Globalization;
using System.Windows.Data;

namespace Client.Utility
{
    public class BoolToOnlineStatusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (bool)value ? "在线" : "离线";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
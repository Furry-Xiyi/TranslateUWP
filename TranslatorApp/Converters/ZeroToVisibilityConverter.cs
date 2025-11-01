using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;

namespace TranslatorApp.Converters
{
    public class ZeroToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is int count)
            {
                return count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            // 兼容 Nullable<int> 或其他数值类型
            if (value is long l) return l == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (value is double d) return Math.Abs(d) < double.Epsilon ? Visibility.Visible : Visibility.Collapsed;

            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
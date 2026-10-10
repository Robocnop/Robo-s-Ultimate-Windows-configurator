using System;
using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace RoboConfigurator
{
    public sealed class SymbolToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            new SymbolIcon { Symbol = (SymbolRegular)value };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

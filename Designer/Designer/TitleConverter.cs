namespace StockSharp.Designer
{
	using System;
	using System.Globalization;
	using System.Windows.Data;

	sealed class TitleConverter : IValueConverter
	{
		private readonly string _prefix;

		public TitleConverter(string prefix)
		{
			if (prefix == null)
				throw new ArgumentNullException(nameof(prefix));

			_prefix = prefix;
		}

		public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
		{
			return $"{_prefix} {value}";
		}

		public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
		{
			throw new NotSupportedException();
		}
	}
}
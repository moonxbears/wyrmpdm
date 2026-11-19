using System;
using System.Collections.Generic;
using System.Globalization;

using HackPDM.Core.Odoo;
using HackPDM.Core.Pdm;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

using Windows.Foundation;
using Windows.UI;

namespace HackPDM.Common;

public sealed partial class FileSizeConverter : IValueConverter
{
	public object Convert( object value, Type targetType, object parameter, string language ) => value switch
	{
		long bytes => ByteSize.Format( bytes, " " ),
		int bytes => ByteSize.Format( bytes, " " ),
		null => string.Empty,
		_ => value,
	};

	public object ConvertBack( object value, Type targetType, object parameter, string language ) => value;
}

public sealed partial class LocalDateTimeConverter : IValueConverter
{
	public object Convert( object? value, Type targetType, object? parameter, string language )
	{
		if( value is not DateTime dt || dt.Year <= 1 )
			return string.Empty;

		DateTime local = dt.Kind switch
		{
			DateTimeKind.Local => dt,
			DateTimeKind.Utc => dt.ToLocalTime(),
			_ => DateTime.SpecifyKind( dt, DateTimeKind.Utc ).ToLocalTime(),
		};
		return local.ToString( "g", CultureInfo.CurrentCulture );
	}

	public object? ConvertBack( object? value, Type targetType, object? parameter, string language ) => value;
}

public sealed partial class OperatorConverter : IValueConverter
{
	public object Convert( object? value, Type targetType, object? parameter, string language )
		=> value is Operators op ? OperatorNames.ToOdoo( op ) : string.Empty;

	public object ConvertBack( object? value, Type targetType, object? parameter, string language )
		=> throw new NotSupportedException();
}

/// <summary>Cached status row tint and foreground brushes adapting to the active WinUI/Uno system theme.</summary>
public static class StatusBrushes
{
	private static readonly Dictionary<(FileStatus Status, bool DoublePainted), Brush?> RowCache = [];
	private static readonly Dictionary<(FileStatus Status, bool Dark), SolidColorBrush?> TextCache = [];

	private static bool IsDarkTheme => Application.Current?.RequestedTheme == ApplicationTheme.Dark;

	public static Brush? ForTableRow( FileStatus status ) => GetRowGradient( status, doublePainted: true );
	public static Brush? ForGroupedRow( FileStatus status ) => GetRowGradient( status, doublePainted: false );

	public static SolidColorBrush? TextBrush( FileStatus status )
	{
		bool dark = IsDarkTheme;
		if( TextCache.TryGetValue( ( status, dark ), out SolidColorBrush? cached ) )
			return cached;

		Color? color = status switch
		{
			FileStatus.Lo => dark ? Color.FromArgb( 255, 0xF5, 0xB9, 0x71 ) : Color.FromArgb( 255, 0xB8, 0x5D, 0x00 ),
			FileStatus.Ro => dark ? Color.FromArgb( 255, 0x79, 0xC8, 0xE8 ) : Color.FromArgb( 255, 0x0B, 0x67, 0xA3 ),
			FileStatus.Dt or FileStatus.Ds => dark ? Color.FromArgb( 255, 0xF4, 0x7B, 0x7B ) : Color.FromArgb( 255, 0xB8, 0x1D, 0x1D ),
			FileStatus.Cm => dark ? Color.FromArgb( 255, 0x7C, 0xD6, 0x7C ) : Color.FromArgb( 255, 0x24, 0x74, 0x24 ),
			FileStatus.Co => dark ? Color.FromArgb( 255, 0xA4, 0xD1, 0x6F ) : Color.FromArgb( 255, 0x4A, 0x6B, 0x1B ),
			FileStatus.Lm => dark ? Color.FromArgb( 255, 0xE8, 0xC8, 0x5A ) : Color.FromArgb( 255, 0x99, 0x6D, 0x00 ),
			FileStatus.Nv => dark ? Color.FromArgb( 255, 0xB4, 0x9A, 0xF7 ) : Color.FromArgb( 255, 0x5B, 0x3D, 0xB8 ),
			_ => null,
		};

		SolidColorBrush? brush = color is { } c ? new SolidColorBrush( c ) : null;
		return TextCache[ ( status, dark ) ] = brush;
	}

	private static Brush? GetRowGradient( FileStatus status, bool doublePainted )
	{
		if( RowCache.TryGetValue( ( status, doublePainted ), out Brush? cached ) )
			return cached;

		(Color Base, byte StartAlpha, byte MidAlpha)? spec = status switch
		{
			FileStatus.Lo => ( Color.FromArgb( 255, 0xFF, 0xA5, 0x00 ), doublePainted ? (byte)28 : (byte)52, doublePainted ? (byte)8 : (byte)14 ),
			FileStatus.Ro => ( Color.FromArgb( 255, 0x1E, 0x90, 0xFF ), doublePainted ? (byte)28 : (byte)52, doublePainted ? (byte)8 : (byte)14 ),
			FileStatus.Dt or FileStatus.Ds => ( Color.FromArgb( 255, 0x8B, 0x00, 0x00 ), doublePainted ? (byte)42 : (byte)76, doublePainted ? (byte)12 : (byte)22 ),
			FileStatus.Cm or FileStatus.Co => ( Color.FromArgb( 255, 0x55, 0x6B, 0x2F ), doublePainted ? (byte)38 : (byte)68, doublePainted ? (byte)10 : (byte)18 ),
			FileStatus.Lm => ( Color.FromArgb( 255, 0xD4, 0xA0, 0x17 ), doublePainted ? (byte)26 : (byte)46, doublePainted ? (byte)7 : (byte)12 ),
			FileStatus.Nv => ( Color.FromArgb( 255, 0x7A, 0x5A, 0xD6 ), doublePainted ? (byte)26 : (byte)46, doublePainted ? (byte)7 : (byte)12 ),
			_ => null,
		};

		if( spec is not var (baseColor, startAlpha, midAlpha) )
			return RowCache[ ( status, doublePainted ) ] = null;

		LinearGradientBrush brush = new()
		{
			StartPoint = new Point( 0, 0.5 ),
			EndPoint = new Point( 1, 0.5 ),
		};
		brush.GradientStops.Add( new GradientStop { Color = Color.FromArgb( startAlpha, baseColor.R, baseColor.G, baseColor.B ), Offset = 0.0 } );
		brush.GradientStops.Add( new GradientStop { Color = Color.FromArgb( midAlpha, baseColor.R, baseColor.G, baseColor.B ), Offset = 0.55 } );
		brush.GradientStops.Add( new GradientStop { Color = Color.FromArgb( 0, baseColor.R, baseColor.G, baseColor.B ), Offset = 1.0 } );
		return RowCache[ ( status, doublePainted ) ] = brush;
	}
}

public sealed partial class StatusRowBackgroundConverter : IValueConverter
{
	public object? Convert( object? value, Type targetType, object? parameter, string language )
		=> value is FileStatus status ? StatusBrushes.ForGroupedRow( status ) : null;
	public object ConvertBack( object? value, Type targetType, object? parameter, string language ) => throw new NotSupportedException();
}

public sealed partial class StatusTextBrushConverter : IValueConverter
{
	public object Convert( object? value, Type targetType, object? parameter, string language )
		=> value is FileStatus status && StatusBrushes.TextBrush( status ) is { } brush
			? brush
			: DependencyProperty.UnsetValue;
	public object ConvertBack( object? value, Type targetType, object? parameter, string language ) => throw new NotSupportedException();
}

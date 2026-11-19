using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using HackPDM.Core.Configuration;

namespace HackPDM.Common;

/// <summary>Opens settings.json in <see cref="AppDataPaths.Config"/>, importing legacy packaged settings on first run.</summary>
public static class AppSettingsStore
{
	public static string FilePath => Path.Combine( AppDataPaths.Config, "settings.json" );

	public static JsonFileSettingsProvider Open()
	{
		JsonFileSettingsProvider settings = new( FilePath );
		if( !settings.Existed && !AppDataPaths.IsConfigOverridden )
			ImportPackagedSettings( settings );
		return settings;
	}

	private static void ImportPackagedSettings( JsonFileSettingsProvider settings )
	{
		try
		{
			List<KeyValuePair<string, object?>> values = Windows.Storage.ApplicationData.Current.LocalSettings.Values
				.Where( v => !v.Key.StartsWith( "__UNO__", StringComparison.Ordinal ) && !v.Key.StartsWith( "__Uno.", StringComparison.Ordinal ) )
				.Select( v => new KeyValuePair<string, object?>( v.Key, UnwrapLocalSettingValue( v.Value ) ) )
				.ToList();
			int imported = settings.ImportMissing( values );
			if( imported > 0 )
				HackLogger.Info( "Settings", $"Imported {imported} value(s) from packaged app settings to '{settings.FilePath}'" );
		}
		catch( Exception e )
		{
			HackLogger.Debug( "Settings", $"No packaged settings to import: {e.Message}" );
		}
	}

	internal static object? UnwrapLocalSettingValue( object? value )
	{
		if( value is not string raw )
			return value;

		int sep = raw.IndexOf( ':' );
		if( sep <= 0 )
			return raw;

		ReadOnlySpan<char> typeName = raw.AsSpan( 0, sep );
		ReadOnlySpan<char> payload = raw.AsSpan( sep + 1 );
		return typeName switch
		{
			"System.String" => payload.ToString(),
			"System.Boolean" when bool.TryParse( payload, out bool b ) => b,
			"System.Int32" when int.TryParse( payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i ) => i,
			"System.Int64" when long.TryParse( payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l ) => l,
			"System.Double" when double.TryParse( payload, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double d ) => d,
			"System.Single" when float.TryParse( payload, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float f ) => f,
			"System.Decimal" when decimal.TryParse( payload, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal m ) => m,
			_ => raw,
		};
	}
}

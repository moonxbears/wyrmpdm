using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

using HackPDM.Abstractions;
using HackPDM.Core.Configuration;
using HackPDM.Core.Hack;
using HackPDM.Core.Logging;
using HackPDM.Core.Theming;
using HackPDM.Domain.Hack;
using HackPDM.Domain.OdooModels;
using HackPDM.Infrastructure.Odoo;
using HackPDM.Infrastructure.Operations;
using HackPDM.Mvvm.Models.Settings;
using HackPDM.Mvvm.ViewModels.Common;
using HackPDM.Mvvm.Views.Common.Controls;
using HackPDM.Mvvm.Views.Common.Settings;
using HackPDM.Mvvm.Views.Common.Theming;
using HackPDM.Mvvm.Views.Dialogs;
using HackPDM.Mvvm.Views.Hack;
using HackPDM.Mvvm.Views.Settings;
using HackPDM.Mvvm.Views.Tools;
using HackPDM.Shared.GlobalData;
using HackPDM.Shell;
using HackPDM.Shell.Mcp;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HackPDM;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class HackApp : Application
{
	public const string RestartWaitPidEnvVar = "HACKPDM_RESTART_WAIT_PID";
	public const string RestartNavEnvVar = "HACKPDM_RESTART_NAV";

	public static HomeView? HomePage;
	public static Window? Window;
	public static Frame? RootFrame;
	public static CoreSettings CoreSettings => AppComposition.Settings;

	public HackApp()
	{
		InitializeComponent();
		RegisterCrashHandlers();

		// menus, flyouts and dialogs follow the application theme, and controls take their corners,
		// density, and typography from the resources, all of which are applied as early as possible
		ThemeSettings startupTheme = ThemeService.ReadSavedThemeSettings();
		var (palette, form) = ThemeService.ResolveStartupPresets( startupTheme );
		RequestedTheme = ThemePresets.Palette( palette ).IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light;
		ThemeService.ApplyStartupResources( Resources, palette, form, startupTheme.Style );
		Setup();
	}

	private void RegisterCrashHandlers()
	{
		UnhandledException += ( _, e ) =>
		{
			string? dump = HackLogger.WriteCrashDump(
				"App.UnhandledException",
				e.Exception,
				ClientUpdate.CurrentText,
				context: $"ActivePage={HomePage?.GetType().Name ?? "none"}" );
			HackLogger.Critical( "App", $"Fatal UI unhandled exception; crash dump: {dump ?? "(unavailable)"}" );
		};

		AppDomain.CurrentDomain.UnhandledException += ( _, e ) =>
		{
			Exception? ex = e.ExceptionObject as Exception;
			HackLogger.WriteCrashDump(
				"AppDomain.UnhandledException",
				ex,
				ClientUpdate.CurrentText,
				context: $"IsTerminating={e.IsTerminating}" );
		};

		TaskScheduler.UnobservedTaskException += ( _, e ) =>
		{
			HackLogger.Error( "TaskScheduler", "Unobserved task exception", e.Exception );
			e.SetObserved();
		};
	}

	/// <summary>Loads the settings, then shows the home page in the main window.</summary>
	private static void ConfigureServices()
	{
		_ = AppComposition.Settings; // load the saved settings before the defaults
		AppComposition.InitializeDefaults();
		HackLogger.Info( "App", $"HackPDM {ClientUpdate.CurrentText} started (UI config loaded={UIStorage.GlobalData.IsLoadedFromFile}, logs={HackLogger.LogDirectory})" );
		HomePage = new HomeView();

		RootFrame = new Frame();
		Window?.Content = RootFrame;
		RootFrame.Content = HomePage;
	}

	/// <summary>
	/// Invoked when the application is launched.
	/// </summary>
	/// <param name="args">Details about the launch request and process.</param>
	protected override void OnLaunched( LaunchActivatedEventArgs args )
	{
		WaitForPreviousRestartPidIfNeeded();

		Window = new MainWindow();
		Window.SetWindowType( AppWindowPresenterKind.Overlapped );
		Window.Closed += Window_Closed;
		WindowHelper.RegisterWindow( Window, "main" );
		Window.Activate();

		AppComposition.Initialize( Window.DispatcherQueue );
		ConfigureServices();
		AppComposition.Theme.Attach( Window );
		McpBridgeHost.Start();

		string? restartNav = Environment.GetEnvironmentVariable( RestartNavEnvVar );
		if( !string.IsNullOrWhiteSpace( restartNav ) )
		{
			Environment.SetEnvironmentVariable( RestartNavEnvVar, null );
			_ = RestoreNavigationTagAsync( restartNav );
		}

		// HACKPDM_SMOKE_TEST=1: drive login -> navigation -> server round trips, then exit (CI)
		if( HackPDM.Shell.Testing.SmokeTestRunner.IsEnabled )
			HackPDM.Shell.Testing.SmokeTestRunner.Start();
	}

	/// <summary>
	/// Rebuilds the root UI tree in-process so updated corners, borders, compact/standard density,
	/// typography, column settings, entry colors, and workspace layouts take full effect live without
	/// disconnecting the embedded MCP server or logging out of Odoo.
	/// </summary>
	internal static async Task<string?> SoftRestartUiAsync( bool saveConfig = true, bool preserveNavigation = true )
	{
		if( Window is null )
			throw new InvalidOperationException( "Main window is not initialized." );

		string? navTag = preserveNavigation ? CaptureNavigationTag() : null;
		if( saveConfig )
		{
			UIStorage.GlobalData.Theme = AppComposition.Theme.Settings;
			AppConfigStore.Save( UIStorage.GlobalData );
		}

		ThemeService.ApplyStartupResources(
			Current.Resources,
			AppComposition.Theme.Palette,
			AppComposition.Theme.Form,
			AppComposition.Theme.Settings.Style );
		AppComposition.Theme.ReapplyLiveResources();

		HomePage = new HomeView();
		RootFrame = new Frame { Content = HomePage };
		Window.Content = RootFrame;
		AppComposition.Theme.Attach( Window );
		AppComposition.Theme.MarkStyleSnapshotApplied();

		if( !string.IsNullOrWhiteSpace( navTag ) )
			await RestoreNavigationTagAsync( navTag );

		HackLogger.Info( "App", $"Soft UI restart completed (restoredNav={navTag ?? "default"})." );
		return navTag;
	}

	/// <summary>
	/// Saves configuration and schedules a clean full OS process restart of HackPDM (needed when
	/// switching the base Dark/Light application theme), optionally preserving the MCP bridge port,
	/// Bearer token, and active page so MCP clients can reconnect seamlessly.
	/// </summary>
	internal static object ScheduleFullRestart(
		bool saveConfig = true,
		bool preserveNavigation = true,
		bool preserveConnection = true,
		int delayMs = 250 )
	{
		string? navTag = preserveNavigation ? CaptureNavigationTag() : null;
		if( saveConfig )
		{
			UIStorage.GlobalData.Theme = AppComposition.Theme.Settings;
			AppConfigStore.Save( UIStorage.GlobalData );
		}

		int oldPid = Environment.ProcessId;
		int? currentPort = McpBridgeHost.Port;
		string? currentToken = McpBridgeHost.AuthToken;
		string discoveryPath = McpBridgeHost.DiscoveryFilePath;

		ProcessStartInfo psi = BuildRelaunchStartInfo(
			oldPid: oldPid,
			navTag: navTag,
			preserveConnection: preserveConnection,
			port: currentPort,
			token: currentToken );

		_ = Task.Run( async () =>
		{
			try
			{
				await Task.Delay( Math.Clamp( delayMs, 50, 5000 ) );
				McpBridgeHost.Stop();
				Process.Start( psi );
				await Task.Delay( 100 );
			}
			catch( Exception ex )
			{
				HackLogger.Error( "App", $"Scheduled full restart failed to spawn replacement process: {ex.Message}", ex );
				return;
			}
			Environment.Exit( 0 );
		} );

		HackLogger.Info( "App", $"Scheduled full HackPDM restart (oldPid={oldPid}, preserveConnection={preserveConnection}, nav={navTag ?? "default"})." );
		return new
		{
			restarted = true,
			mode = "full",
			scheduledFullRestart = true,
			oldPid,
			preservedPort = preserveConnection ? currentPort : null,
			preserveConnection,
			preservedNavigation = navTag,
			discoveryFilePath = discoveryPath,
			commandLine = $"{psi.FileName} {string.Join( " ", psi.ArgumentList )}".Trim(),
		};
	}

	internal static string? CaptureNavigationTag()
	{
		object? content = HomePage?.ContentFrame?.Content;
		return content switch
		{
			HackFileManagerView => nameof( NavigatePageMenu.HackFileManager ),
			DependencyGraphView => nameof( NavigatePageMenu.DependencyGraph ),
			ProfileManagerView => nameof( NavigatePageMenu.ProfileManager ),
			ConfigurationView => nameof( NavigatePageMenu.Configuration ),
			JsonEditorView => nameof( NavigatePageMenu.JsonEditor ),
			ApplicationSettingsView settings => settings.ActiveSectionPage switch
			{
				McpSettingsView => $"{nameof( NavigatePageMenu.Settings )}:Mcp",
				StatusSettingsView => $"{nameof( NavigatePageMenu.Settings )}:Status",
				_ => $"{nameof( NavigatePageMenu.Settings )}:Theme",
			},
			_ => null,
		};
	}

	internal static async Task RestoreNavigationTagAsync( string navTag )
	{
		try
		{
			string[] parts = navTag.Split( ':', 2, StringSplitOptions.TrimEntries );
			if( !Enum.TryParse<NavigatePageMenu>( parts[ 0 ], ignoreCase: true, out NavigatePageMenu target ) )
				return;

			await Task.Delay( 80 );
			AppComposition.Navigation.NavigateTo( target );

			if( target == NavigatePageMenu.Settings && parts.Length == 2 && !string.IsNullOrWhiteSpace( parts[ 1 ] ) )
			{
				for( int i = 0; i < 40; i++ )
				{
					if( HomePage?.ContentFrame?.Content is ApplicationSettingsView settingsView )
					{
						await Task.Delay( 60 );
						settingsView.SelectSection( parts[ 1 ] );
						break;
					}
					await Task.Delay( 50 );
				}
			}
		}
		catch( Exception ex )
		{
			HackLogger.Warn( "App", $"Could not restore navigation '{navTag}' after restart: {ex.Message}" );
		}
	}

	private static ProcessStartInfo BuildRelaunchStartInfo(
		int oldPid,
		string? navTag,
		bool preserveConnection,
		int? port,
		string? token )
	{
		string processPath = Environment.ProcessPath
			?? throw new InvalidOperationException( "Unable to determine current HackPDM process executable path." );
		string[] cmdArgs = Environment.GetCommandLineArgs();
		string exeName = Path.GetFileNameWithoutExtension( processPath );
		bool viaDotnetHost = string.Equals( exeName, "dotnet", StringComparison.OrdinalIgnoreCase );

		ProcessStartInfo psi = new()
		{
			FileName = processPath,
			UseShellExecute = false,
			WorkingDirectory = Environment.CurrentDirectory,
		};

		if( viaDotnetHost )
		{
			// cmdArgs[0] is the managed entry assembly path (HackPDM.dll)
			for( int i = 0; i < cmdArgs.Length; i++ )
				psi.ArgumentList.Add( cmdArgs[ i ] );
		}
		else
		{
			for( int i = 1; i < cmdArgs.Length; i++ )
				psi.ArgumentList.Add( cmdArgs[ i ] );
		}

		psi.Environment[ RestartWaitPidEnvVar ] = oldPid.ToString();
		if( !string.IsNullOrWhiteSpace( navTag ) )
			psi.Environment[ RestartNavEnvVar ] = navTag;
		else
			psi.Environment.Remove( RestartNavEnvVar );

		if( preserveConnection && port is > 0 )
		{
			psi.Environment[ McpBridgeHost.EnabledEnvVar ] = "1";
			psi.Environment[ McpBridgeHost.PortEnvVar ] = port.Value.ToString();
			if( !string.IsNullOrEmpty( token ) )
				psi.Environment[ McpBridgeHost.TokenEnvVar ] = token;
		}

		return psi;
	}

	private static void WaitForPreviousRestartPidIfNeeded()
	{
		string? rawPid = Environment.GetEnvironmentVariable( RestartWaitPidEnvVar );
		if( string.IsNullOrWhiteSpace( rawPid ) )
			return;
		Environment.SetEnvironmentVariable( RestartWaitPidEnvVar, null );

		if( !int.TryParse( rawPid, out int oldPid ) || oldPid <= 0 || oldPid == Environment.ProcessId )
			return;

		try
		{
			using Process prev = Process.GetProcessById( oldPid );
			prev.WaitForExit( 5000 );
		}
		catch
		{
			// Already exited
		}
	}

	private void Window_Closed( object sender, WindowEventArgs args )
	{
		McpBridgeHost.Stop();
		AppConfigStore.Save( UIStorage.GlobalData );
	}

	private static void Setup()
	{
		if( StorageBox.TemporaryPath == null ) return;
		if( !Directory.Exists( StorageBox.TemporaryPath ) ) Directory.CreateDirectory( StorageBox.TemporaryPath );
	}
}

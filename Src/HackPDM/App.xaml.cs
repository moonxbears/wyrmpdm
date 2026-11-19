using System;
using System.IO;
using System.Threading.Tasks;

using HackPDM.Common;
using HackPDM.Core.Configuration;
using HackPDM.Core.Pdm;
using HackPDM.Shell;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HackPDM;

public partial class HackApp : Application
{
	public static HomeView? HomePage { get; private set; }
	public static Window? Window { get; private set; }

	public HackApp()
	{
		InitializeComponent();
		RegisterCrashHandlers();
		Directory.CreateDirectory( PdmConstants.TemporaryPath );
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
			HackLogger.WriteCrashDump(
				"AppDomain.UnhandledException",
				e.ExceptionObject as Exception,
				ClientUpdate.CurrentText,
				context: $"IsTerminating={e.IsTerminating}" );
		};

		TaskScheduler.UnobservedTaskException += ( _, e ) =>
		{
			HackLogger.Error( "TaskScheduler", "Unobserved task exception", e.Exception );
			e.SetObserved();
		};
	}

	protected override void OnLaunched( LaunchActivatedEventArgs args )
	{
		Window = new MainWindow();
		Window.SetWindowType( AppWindowPresenterKind.Overlapped );
		Window.Closed += ( _, _ ) => AppComposition.LocalFiles.Dispose();
		Window.Activate();

		AppComposition.Initialize( Window.DispatcherQueue );
		AppComposition.InitializeDefaults();
		HackLogger.Info( "App", $"HackPDM {ClientUpdate.CurrentText} started (logs={HackLogger.LogDirectory})" );

		HomePage = new HomeView();
		Window.Content = new Frame { Content = HomePage };
	}
}

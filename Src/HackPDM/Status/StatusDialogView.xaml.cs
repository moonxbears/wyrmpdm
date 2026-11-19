using System;
using System.Threading.Tasks;

using HackPDM.Common;
using HackPDM.Core.Configuration;
using HackPDM.Core.Pdm;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HackPDM.Status;

public sealed partial class StatusDialogView : Page
{
	private bool _allowClose;
	private bool _isClosing;

	public StatusDialogViewModel ViewModel => AppComposition.StatusLog;

	public Window? ParentWindow
	{
		get;
		set
		{
			if( ReferenceEquals( field, value ) )
				return;
			if( field is not null )
				field.AppWindow.Closing -= AppWindow_Closing;
			field = value;
			if( field is not null )
			{
				_allowClose = false;
				field.AppWindow.Closing += AppWindow_Closing;
			}
		}
	}

	public StatusDialogView()
	{
		InitializeComponent();
		Loaded += OnLoaded;
		Unloaded += ( _, _ ) => ViewModel.OperationCompleted -= OnOperationCompleted;
	}

	private void OnLoaded( object sender, RoutedEventArgs e )
	{
		ParentWindow ??= WindowHelper.WindowOf( this );
		ViewModel.OperationCompleted -= OnOperationCompleted;
		ViewModel.OperationCompleted += OnOperationCompleted;
	}

	private void OnOperationCompleted()
	{
		if( ViewModel.AutoClose )
			CloseImmediately();
	}

	private void CloseImmediately()
	{
		_allowClose = true;
		( ParentWindow ?? WindowHelper.WindowOf( this ) )?.Close();
	}

	private async Task CloseWindowAsync()
	{
		if( _isClosing )
			return;
		_isClosing = true;
		try
		{
			if( await ViewModel.ConfirmCloseAsync() )
				CloseImmediately();
		}
		finally
		{
			_isClosing = false;
		}
	}

	private async void Close_Click( object sender, RoutedEventArgs e ) => await CloseWindowAsync();

	private async void AppWindow_Closing( AppWindow sender, AppWindowClosingEventArgs args )
	{
		if( _allowClose || !ViewModel.IsInProcess )
			return;
		args.Cancel = true;
		await CloseWindowAsync();
	}

	private void HorizontalSplitter_DragDelta( double delta )
	{
		double totalH = OperationsRow.ActualHeight + LowerLogsRow.ActualHeight;
		if( totalH <= 180 )
			return;
		double top = Math.Clamp( OperationsRow.ActualHeight + delta, 100, totalH - 100 );
		OperationsRow.Height = new GridLength( top, GridUnitType.Star );
		LowerLogsRow.Height = new GridLength( Math.Max( 100, totalH - top ), GridUnitType.Star );
	}

	private void VerticalSplitter_DragDelta( double delta )
	{
		double totalW = InfoLogCol.ActualWidth + ErrorLogCol.ActualWidth;
		if( totalW <= 260 )
			return;
		double left = Math.Clamp( InfoLogCol.ActualWidth + delta, 160, totalW - 160 );
		InfoLogCol.Width = new GridLength( left, GridUnitType.Star );
		ErrorLogCol.Width = new GridLength( Math.Max( 160, totalW - left ), GridUnitType.Star );
	}

	private void CopyDiagnostics_Click( object sender, RoutedEventArgs e )
	{
		try
		{
			var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
			package.SetText( ViewModel.BuildDiagnosticSummary() );
			Windows.ApplicationModel.DataTransfer.Clipboard.SetContent( package );
			ViewModel.AddLog( ( StatusMessage.INFO, "Copied diagnostic summary to clipboard." ) );
		}
		catch( Exception ex )
		{
			HackLogger.Warn( "StatusDialog", "Failed to copy diagnostic summary to clipboard", ex );
		}
	}
}

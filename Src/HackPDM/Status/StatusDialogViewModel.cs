using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using HackPDM.Common;
using HackPDM.Core.Configuration;
using HackPDM.Core.Odoo;
using HackPDM.Core.Pdm;
using HackPDM.Core.Services;

using Microsoft.UI.Xaml.Data;

namespace HackPDM.Status;

/// <summary>Tracks operation log entries, transfer counters, and progress with coalesced UI-thread updates.</summary>
[Bindable]
public partial class StatusDialogViewModel : ObservableObject
{
	private const int MaxHistory = 5000;
	private const int MaxLogsPerUiTick = 200;

	private readonly Lock _gate = new();
	private readonly Action _cancelOperation;
	private readonly IDialogService _dialogs;
	private readonly ConcurrentQueue<BasicStatusMessage> _pendingLogs = new();
	private int _refreshQueued;
	private int _logFlushQueued;
	private bool _isInProcess;
	private bool _autoClose;

	public event Action? OperationCompleted;

	public StatusDialogViewModel( IDialogService dialogs, Action cancelOperation )
	{
		_dialogs = dialogs;
		_cancelOperation = cancelOperation;
		_autoClose = HackDefaults.Instance.SettingsProvider?.Get( "StatusAutoClose", false ) ?? false;
	}

	public ObservableCollection<BasicStatusMessage> OStatus { get; } = [];
	public ObservableCollection<BasicStatusMessage> OInfo { get; } = [];
	public ObservableCollection<BasicStatusMessage> OError { get; } = [];

	public bool IsInProcess
	{
		get { lock( _gate ) return _isInProcess; }
		set => SetInProcess( value );
	}

	public bool AutoClose
	{
		get => _autoClose;
		set
		{
			if( !SetProperty( ref _autoClose, value ) )
				return;
			HackDefaults.Instance.SettingsProvider?.Set( "StatusAutoClose", value );
		}
	}

	public int MaxCount { get; private set; }
	public int TotalProcessed { get; private set; }
	public int SkipCounter { get; private set; }
	private int ProcessCounter { get; set; }
	public long DownloadedBytes { get; private set; }
	public long SessionDownloadedBytes { get; private set; }
	public long UploadedBytes { get; private set; }
	public long SessionUploadedBytes { get; private set; }

	public string ProgressText
	{
		get
		{
			float percent = MaxCount > 0 ? TotalProcessed / (float)MaxCount * 100f : 0f;
			return $"({percent:f2}%)\n{TotalProcessed} / {MaxCount}";
		}
	}

	public string SkippedText => $"({SkipCounter}) Skipped";
	public string DownloadedText => $"Downloaded: {ByteSize.Format( DownloadedBytes )}";
	public string SessionDownloadedText => $"Session Downloaded: {ByteSize.Format( SessionDownloadedBytes )}";
	public string UploadedText => $"Uploaded: {ByteSize.Format( UploadedBytes )}";
	public string SessionUploadedText => $"Session Uploaded: {ByteSize.Format( SessionUploadedBytes )}";

	public void AddLog( BasicStatusMessage message )
	{
		_pendingLogs.Enqueue( message );
		ScheduleLogFlush();
	}

	private void ScheduleLogFlush()
	{
		if( Interlocked.Exchange( ref _logFlushQueued, 1 ) == 1 )
			return;
		UiDispatcher.Post( FlushPendingLogs );
	}

	private void FlushPendingLogs()
	{
		Volatile.Write( ref _logFlushQueued, 0 );
		int drained = 0;
		while( drained < MaxLogsPerUiTick && _pendingLogs.TryDequeue( out BasicStatusMessage? message ) )
		{
			ObservableCollection<BasicStatusMessage> target = message.Status switch
			{
				StatusMessage.PROCESSING or StatusMessage.SUCCESS => OStatus,
				StatusMessage.WARNING or StatusMessage.ERROR => OError,
				_ => OInfo,
			};
			if( target.Count >= MaxHistory )
				target.RemoveAt( 0 );
			target.Add( message );
			drained++;
		}
		if( !_pendingLogs.IsEmpty )
			ScheduleLogFlush();
	}

	[RelayCommand( CanExecute = nameof( CanCancel ) )]
	private void Cancel() => _cancelOperation();

	private bool CanCancel() => IsInProcess;

	public void SetInProcess( bool inProcess )
	{
		lock( _gate )
		{
			if( _isInProcess == inProcess )
				return;
			_isInProcess = inProcess;
		}
		UiDispatcher.Post( () =>
		{
			OnPropertyChanged( nameof( IsInProcess ) );
			CancelCommand.NotifyCanExecuteChanged();
		} );
	}

	[RelayCommand]
	public void ClearLogs()
	{
		if( IsInProcess )
			return;
		lock( _gate )
		{
			( MaxCount, ProcessCounter, SkipCounter, TotalProcessed ) = ( 0, 0, 0, 0 );
			DownloadedBytes = 0;
			UploadedBytes = 0;
		}
		UiDispatcher.Post( () =>
		{
			while( _pendingLogs.TryDequeue( out _ ) ) { }
			OStatus.Clear();
			OInfo.Clear();
			OError.Clear();
			NotifyProgressChanged();
		} );
	}

	public async Task<bool> ConfirmCloseAsync()
	{
		if( !IsInProcess )
			return true;
		bool close = DialogResult.OK == await _dialogs.ShowMessageAsync(
			"There are items still processing.\nCancel the running operation and close this window?",
			"Cancel Operation?",
			MessageBoxButtons.OKCancel,
			MessageBoxIcon.Warning );
		if( close )
		{
			_cancelOperation();
			SetInProcess( false );
		}
		return close;
	}

	public void ResetProgress( int total ) => Update( () =>
	{
		( MaxCount, ProcessCounter, SkipCounter, TotalProcessed ) = ( total, 0, 0, 0 );
		DownloadedBytes = 0;
		UploadedBytes = 0;
	} );

	public void AddDownloadedBytes( long bytes )
	{
		if( bytes <= 0 )
			return;
		Update( () =>
		{
			DownloadedBytes += bytes;
			SessionDownloadedBytes += bytes;
		} );
	}

	public void AddUploadedBytes( long bytes )
	{
		if( bytes <= 0 )
			return;
		Update( () =>
		{
			UploadedBytes += bytes;
			SessionUploadedBytes += bytes;
		} );
	}

	public void Advance( int processed, int skipped ) => Update( () =>
	{
		ProcessCounter += processed;
		SkipCounter += skipped;
		TotalProcessed = ProcessCounter + SkipCounter;
	} );

	public void SetProcessed( int processed ) => Update( () =>
	{
		ProcessCounter = processed;
		TotalProcessed = ProcessCounter + SkipCounter;
	} );

	public void CompleteProgress()
	{
		bool wasInProcess;
		lock( _gate )
		{
			wasInProcess = _isInProcess;
			ProcessCounter = Math.Max( MaxCount - SkipCounter, 0 );
			TotalProcessed = MaxCount;
			_isInProcess = false;
		}
		UiDispatcher.Post( () =>
		{
			NotifyProgressChanged();
			if( wasInProcess )
			{
				OnPropertyChanged( nameof( IsInProcess ) );
				CancelCommand.NotifyCanExecuteChanged();
				OperationCompleted?.Invoke();
			}
		} );
	}

	[RelayCommand]
	private void OpenLogsFolder()
	{
		string dir = HackLogger.LogDirectory;
		try
		{
			Directory.CreateDirectory( dir );
			ShellLauncher.OpenFolder( dir );
		}
		catch( Exception ex )
		{
			HackLogger.Warn( "StatusDialog", $"Could not open logs folder '{dir}'", ex );
		}
	}

	public string BuildDiagnosticSummary()
	{
		OdooDefaults defaults = OdooDefaults.Instance;
		return HackLogger.BuildDiagnosticSummary(
			clientVersion: ClientUpdate.CurrentText,
			odooUrl: defaults.OdooUrl,
			odooDb: defaults.OdooDb );
	}

	private void Update( Action change )
	{
		lock( _gate )
			change();
		if( Interlocked.Exchange( ref _refreshQueued, 1 ) == 1 )
			return;
		UiDispatcher.Post( () =>
		{
			Volatile.Write( ref _refreshQueued, 0 );
			NotifyProgressChanged();
		} );
	}

	private void NotifyProgressChanged()
	{
		OnPropertyChanged( nameof( MaxCount ) );
		OnPropertyChanged( nameof( TotalProcessed ) );
		OnPropertyChanged( nameof( ProgressText ) );
		OnPropertyChanged( nameof( SkippedText ) );
		OnPropertyChanged( nameof( DownloadedText ) );
		OnPropertyChanged( nameof( SessionDownloadedText ) );
		OnPropertyChanged( nameof( UploadedText ) );
		OnPropertyChanged( nameof( SessionUploadedText ) );
	}
}

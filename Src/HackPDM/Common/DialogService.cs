using System.Threading.Tasks;

using HackPDM.Core.Pdm;
using HackPDM.Search;
using HackPDM.Status;

using Microsoft.UI.Xaml;

namespace HackPDM.Common;

public interface IDialogService
{
	Task<DialogResult> ShowMessageAsync(
		string message,
		string? caption = null,
		MessageBoxButtons buttons = MessageBoxButtons.OK,
		MessageBoxIcon icon = MessageBoxIcon.Information );

	Task<bool> ConfirmAsync( string message, string caption, MessageBoxIcon icon = MessageBoxIcon.Question );
	void ShowStatusWindow();
	void ToggleStatusWindow();
	void ShowSearchWindow();
	void ToggleSearchWindow();
}

/// <summary>Opens message boxes and single-instance Status Log / Search Vault windows.</summary>
public sealed class DialogService : IDialogService
{
	private Window? _statusWindow;
	private Window? _searchWindow;

	public Task<DialogResult> ShowMessageAsync(
		string message,
		string? caption = null,
		MessageBoxButtons buttons = MessageBoxButtons.OK,
		MessageBoxIcon icon = MessageBoxIcon.Information )
		=> MessageBox.ShowAsync( message, caption, buttons, icon );

	public async Task<bool> ConfirmAsync( string message, string caption, MessageBoxIcon icon = MessageBoxIcon.Question )
		=> await ShowMessageAsync( message, caption, MessageBoxButtons.YesNo, icon ) == DialogResult.Yes;

	public void ShowStatusWindow()
	{
		UiDispatcher.Post( () =>
		{
			if( _statusWindow is not null )
			{
				_statusWindow.Activate();
				return;
			}
			WindowHelper.CreateWindowAndPage<StatusDialogView>( out StatusDialogView page, out Window window );
			page.ParentWindow = window;
			window.Closed += ( _, _ ) => _statusWindow = null;
			_statusWindow = window;
		} );
	}

	public void ToggleStatusWindow()
	{
		UiDispatcher.Post( () =>
		{
			if( _statusWindow is { } open )
				open.Close();
			else
				ShowStatusWindow();
		} );
	}

	public void ShowSearchWindow()
	{
		UiDispatcher.Post( () =>
		{
			if( AppComposition.Session.IsVersionMismatch )
				return;
			if( _searchWindow is not null )
			{
				_searchWindow.Activate();
				return;
			}
			WindowHelper.CreateWindowAndPage<SearchOdooView>( out SearchOdooView page, out Window window );
			page.ParentWindow = window;
			window.Closed += ( _, _ ) => _searchWindow = null;
			_searchWindow = window;
		} );
	}

	public void ToggleSearchWindow()
	{
		UiDispatcher.Post( () =>
		{
			if( _searchWindow is { } open )
				open.Close();
			else
				ShowSearchWindow();
		} );
	}
}

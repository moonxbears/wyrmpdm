using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using HackPDM.Common;
using HackPDM.Core.Configuration;
using HackPDM.Core.Odoo;
using HackPDM.Core.Pdm;
using HackPDM.Core.Services;

namespace HackPDM.Login;

/// <summary>Verifies client compatibility against server <c>client_version</c> and offers SHA-256-verified updates.</summary>
internal static class HackUpdater
{
	private static string? Setting( string name )
		=> OdooDefaults.Instance.HpSettings?.FirstOrDefault( s => s.name == name )?.char_value;

	public static string? RequiredVersionRequirement => Setting( PdmConstants.ClientVersionKey );

	public static async Task<bool> EnsureUpdatedAsync( bool showOutdatedUpdater = false )
	{
		VersionCheckResult check = ClientUpdate.CheckVersion( ClientUpdate.Current, RequiredVersionRequirement );
		if( check.IsAllowed )
			return true;

		HackLogger.Warn( "Updater", $"Client version check denied access: {check.Reason}" );
		if( showOutdatedUpdater && await AppComposition.Dialogs.ShowMessageAsync(
				$"{check.Reason}\nAccess is denied until you update HackPDM to a compatible version.\n\nDownload and install the published version now?",
				"Version Mismatch — Update Required",
				MessageBoxButtons.YesNo,
				MessageBoxIcon.Warning ) == DialogResult.Yes )
		{
			await InstallUpdateAsync();
		}
		return false;
	}

	public static async Task InstallUpdateAsync()
	{
		IDialogService dialogs = AppComposition.Dialogs;
		string? url = Setting( PdmConstants.ClientDownloadUrlKey );
		HackLogger.Info( "Updater", $"Starting client update download from '{url ?? "(unset)"}'" );
		Result<string> installer = await ClientUpdate.DownloadInstallerAsync( url, Setting( PdmConstants.ClientDownloadSha256Key ) );
		if( !installer.Ok )
		{
			HackLogger.Error( "Updater", $"Client update aborted: {installer.Error}" );
			await dialogs.ShowMessageAsync( $"The update was not installed: {installer.Error}", "Update", MessageBoxButtons.OK, MessageBoxIcon.Warning );
			return;
		}

		HackLogger.Info( "Updater", $"Verified update package ready at '{installer.Value}'" );
		bool started = OperatingSystem.IsWindows()
			? ShellLauncher.OpenFile( installer.Value! )
			: ShellLauncher.OpenFolder( Path.GetDirectoryName( installer.Value! )! );
		if( !started )
		{
			HackLogger.Warn( "Updater", $"Could not launch verified update at '{installer.Value}'" );
			await dialogs.ShowMessageAsync(
				$"The update was downloaded to {installer.Value} but could not be opened; run it from there.",
				"Update",
				MessageBoxButtons.OK,
				MessageBoxIcon.Warning );
			return;
		}

		if( OperatingSystem.IsWindows() )
		{
			HackLogger.Info( "Updater", "Installer launched; exiting HackPDM for upgrade." );
			HackApp.Current.Exit();
		}
		else
		{
			await dialogs.ShowMessageAsync(
				$"The checked update is in {installer.Value}. Close HackPDM and install it.",
				"Update",
				MessageBoxButtons.OK,
				MessageBoxIcon.Information );
		}
	}
}

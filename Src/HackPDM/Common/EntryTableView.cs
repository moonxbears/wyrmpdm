using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

using WinUI.TableView;

namespace HackPDM.Common;

/// <summary>TableView wrapper fixing WinUI.TableView 1.5.1 selection and drag-jitter quirks on WinUI/Uno.</summary>
public partial class EntryTableView : TableView
{
	private const double DragThresholdSq = 64.0;
	private Point _pressPoint;
	private bool _leftPressActive;
	private bool _dragThresholdMet;

	protected override void OnPointerPressed( PointerRoutedEventArgs e )
	{
		var point = e.GetCurrentPoint( this );
		if( point.Properties.IsLeftButtonPressed )
		{
			_pressPoint = point.Position;
			_leftPressActive = true;
			_dragThresholdMet = false;

			if( SelectionMode == ListViewSelectionMode.Extended
				&& !HasMultiSelectModifier( e )
				&& FindAncestorRow( e.OriginalSource as DependencyObject ) is { } row
				&& ItemFromContainer( row ) is { } clickedItem
				&& SelectedItems.Count > 0
				&& ( SelectedItems.Count > 1 || !ReferenceEquals( SelectedItems[ 0 ], clickedItem ) ) )
			{
				SelectedItems.Clear();
			}
		}

		base.OnPointerPressed( e );
	}

	protected override void OnPointerMoved( PointerRoutedEventArgs e )
	{
		if( _leftPressActive )
		{
			if( !ShowDragRectangle )
				return;

			if( !_dragThresholdMet )
			{
				Point now = e.GetCurrentPoint( this ).Position;
				double dx = now.X - _pressPoint.X;
				double dy = now.Y - _pressPoint.Y;
				if( dx * dx + dy * dy < DragThresholdSq )
					return;
				_dragThresholdMet = true;
			}
		}

		base.OnPointerMoved( e );
	}

	protected override void OnPointerReleased( PointerRoutedEventArgs e )
	{
		_leftPressActive = false;
		_dragThresholdMet = false;
		base.OnPointerReleased( e );
	}

	protected override void OnPointerCaptureLost( PointerRoutedEventArgs e )
	{
		if( _leftPressActive )
		{
			_leftPressActive = false;
			_dragThresholdMet = false;
			base.OnPointerReleased( e );
		}
		base.OnPointerCaptureLost( e );
	}

	protected override void OnPointerCanceled( PointerRoutedEventArgs e )
	{
		if( _leftPressActive )
		{
			_leftPressActive = false;
			_dragThresholdMet = false;
			base.OnPointerReleased( e );
		}
		base.OnPointerCanceled( e );
	}

	private static bool HasMultiSelectModifier( PointerRoutedEventArgs e )
	{
		if( ( e.KeyModifiers & ( VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift ) ) != 0 )
			return true;
		var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread( VirtualKey.Control );
		var shift = InputKeyboardSource.GetKeyStateForCurrentThread( VirtualKey.Shift );
		return ctrl.HasFlag( CoreVirtualKeyStates.Down ) || shift.HasFlag( CoreVirtualKeyStates.Down );
	}

	private TableViewRow? FindAncestorRow( DependencyObject? source )
	{
		DependencyObject? current = source;
		while( current is not null && !ReferenceEquals( current, this ) )
		{
			if( current is TableViewRow row )
				return row;
			if( current is TableViewHeaderRow or TableViewColumnHeader or ScrollBar )
				return null;
			current = VisualTreeHelper.GetParent( current );
		}
		return null;
	}
}

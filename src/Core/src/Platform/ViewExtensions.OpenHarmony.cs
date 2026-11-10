using System;

namespace Microsoft.Maui.Platform;

/// <summary>
/// Core-level platform-view helpers for OpenHarmony. The compositor owns the real view tree, so
/// load/unload notifications complete immediately and soft-input operations are no-ops here; the
/// slice's keyboard/input router drives the actual platform behavior.
/// </summary>
public static partial class ViewExtensions
{
	internal static bool IsLoaded(this object view) => true;

	internal static IDisposable OnLoaded(this object view, Action action)
	{
		action();
		return new EmptySubscription();
	}

	internal static IDisposable OnUnloaded(this object view, Action action) => new EmptySubscription();

	internal static bool HideSoftInput(this object view) => false;

	internal static bool ShowSoftInput(this object view) => false;

	internal static bool IsSoftInputShowing(this object view) => false;

	public static void UpdateToolTip(this object platformView, ToolTip? tooltip)
	{
	}

	private sealed class EmptySubscription : IDisposable
	{
		public void Dispose()
		{
		}
	}
}

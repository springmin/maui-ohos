using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Handlers
{
	// The OpenHarmony compositor has no native container hierarchy: every handler arranges its
	// virtual view through the compositor. These are the platform-neutral defaults the non-generic
	// ViewHandler requires; concrete handlers override GetDesiredSize/PlatformArrange where the
	// platform view measures or arranges children.
	public abstract partial class ViewHandler<TVirtualView, TPlatformView>
	{
		public override void PlatformArrange(Rect rect)
		{
		}

		public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
			=> Size.Zero;

		protected override void SetupContainer()
		{
		}

		protected override void RemoveContainer()
		{
		}
	}
}

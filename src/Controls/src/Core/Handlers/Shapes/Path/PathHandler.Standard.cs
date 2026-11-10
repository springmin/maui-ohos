#nullable disable
using System;
using Microsoft.Maui.Controls.Shapes;
using Path = Microsoft.Maui.Controls.Shapes.Path;

namespace Microsoft.Maui.Controls.Handlers
{
	public partial class PathHandler
	{
		public static void MapShape(IShapeViewHandler handler, Path path) { }
		public static void MapData(IShapeViewHandler handler, Path path) { }
		public static void MapRenderTransform(IShapeViewHandler handler, Path path) { }
	}
}
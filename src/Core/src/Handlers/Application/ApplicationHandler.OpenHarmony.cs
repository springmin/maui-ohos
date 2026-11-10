namespace Microsoft.Maui.Handlers
{
	public partial class ApplicationHandler : ElementHandler<IApplication, object>
	{
		public static partial void MapTerminate(ApplicationHandler handler, IApplication application, object? args) { }
		public static partial void MapOpenWindow(ApplicationHandler handler, IApplication application, object? args) { }
		public static partial void MapCloseWindow(ApplicationHandler handler, IApplication application, object? args) { }
		public static partial void MapActivateWindow(ApplicationHandler handler, IApplication application, object? args) { }
	}
}

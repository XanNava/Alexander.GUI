using Uno.UI.Hosting;

namespace Alexander.Gui;

// Desktop entry point. Uno picks the first host that works on the
// machine it's running on: X11 or the Linux framebuffer, macOS, or Win32.
internal static class Program {
	[STAThread]
	public static void Main(string[] args) {
		var host = UnoPlatformHostBuilder.Create()
			.App(() => new App())
			.UseX11()
			.UseLinuxFrameBuffer()
			.UseMacOS()
			.UseWin32()
			.Build();

		host.Run();
	}
}

using System.Diagnostics;

namespace Alexander.Gui;

// The few things that differ per operating system. Everything is started
// on the thread pool by callers - launching processes can be slow.
internal static class PlatformShell {
	public static StringComparison PathComparison =>
		OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

	public static string FileManagerName =>
		OperatingSystem.IsWindows() ? "Explorer" : OperatingSystem.IsMacOS() ? "Finder" : "file manager";

	// Shows a folder in the OS file manager.
	public static void RevealFolder(string path) {
		var startInfo = new ProcessStartInfo(
			OperatingSystem.IsWindows() ? "explorer.exe"
			: OperatingSystem.IsMacOS() ? "open"
			: "xdg-open") { UseShellExecute = false };

		startInfo.ArgumentList.Add(path);
		Process.Start(startInfo);
	}

	// Runs a program in a new terminal window, in folder.
	public static void OpenInTerminal(string fileName, IReadOnlyList<string> arguments, string folder) {
		if (OperatingSystem.IsWindows()) {
			// A console program started through the shell gets its own console.
			Process.Start(new ProcessStartInfo(fileName, string.Join(' ', arguments.Select(WindowsQuote))) {
				UseShellExecute = true,
				WorkingDirectory = folder
			});

			return;
		}

		var command = $"cd {PosixQuote(folder)} && {PosixQuote(fileName)} {string.Join(' ', arguments.Select(PosixQuote))}";

		if (OperatingSystem.IsMacOS()) {
			var script = command.Replace("\\", "\\\\").Replace("\"", "\\\"");
			var startInfo = new ProcessStartInfo("osascript") { UseShellExecute = false };
			startInfo.ArgumentList.Add("-e");
			startInfo.ArgumentList.Add($"tell application \"Terminal\" to do script \"{script}\"");
			startInfo.ArgumentList.Add("-e");
			startInfo.ArgumentList.Add("tell application \"Terminal\" to activate");
			Process.Start(startInfo);
			return;
		}

		// Linux: the first terminal emulator that's installed.
		(string Program, string[] Prefix)[] terminals = [
			("x-terminal-emulator", ["-e"]),
			("gnome-terminal", ["--"]),
			("konsole", ["-e"]),
			("xfce4-terminal", ["-x"]),
			("kitty", []),
			("alacritty", ["-e"]),
			("xterm", ["-e"])
		];

		var (program, prefix) = terminals.FirstOrDefault(terminal => IsOnPath(terminal.Program));

		if (program is null) {
			throw new InvalidOperationException("No terminal emulator found (tried x-terminal-emulator, gnome-terminal, konsole, xfce4-terminal, kitty, alacritty, xterm).");
		}

		var linux = new ProcessStartInfo(program) { UseShellExecute = false, WorkingDirectory = folder };

		foreach (var part in prefix) {
			linux.ArgumentList.Add(part);
		}

		linux.ArgumentList.Add("sh");
		linux.ArgumentList.Add("-c");
		linux.ArgumentList.Add(command);
		Process.Start(linux);
	}

	private static bool IsOnPath(string program) {
		var path = Environment.GetEnvironmentVariable("PATH") ?? "";
		return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
			.Any(folder => File.Exists(Path.Combine(folder, program)));
	}

	private static string PosixQuote(string value) {
		return $"'{value.Replace("'", "'\\''")}'";
	}

	private static string WindowsQuote(string value) {
		return value.Contains(' ') || value.Contains('"') ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
	}
}

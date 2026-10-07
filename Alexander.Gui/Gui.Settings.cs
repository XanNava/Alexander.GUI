using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alexander.Gui;

internal static class PanelKinds {
	public const string Command = "command";
	public const string Explorer = "explorer";
	public const string Settings = "settings";
	public const string View = "view";
}

// User preferences - edited in the Settings panel, saved to settings.json.
internal sealed class GuiSettings {
	// "dark" or "light".
	public string Theme { get; set; } = "dark";

	// Preferred monospace fonts, first installed one wins (per machine -
	// the list covers Windows, macOS and Linux defaults).
	public string FontFamily { get; set; } = "Cascadia Mono, Consolas, SF Mono, Menlo, JetBrains Mono, DejaVu Sans Mono, Liberation Mono";
	public double FontSize { get; set; } = 13;

	// Oldest lines are dropped past this, per command window.
	public int MaxOutputLines { get; set; } = 2000;

	// Commands remembered per command window (Up/Down), saved with the layout.
	public int HistorySize { get; set; } = 200;

	// Reopen the panels and docking layout from last time.
	public bool RestoreLayout { get; set; } = true;

	public bool ShowHiddenFolders { get; set; }

	// Folder a new Shell starts in when it has nothing better; null means
	// the folder Alexander.Gui was launched from.
	public string? StartFolder { get; set; }

	// Prefilled in the folder explorer's "Run" box.
	public string ExplorerCommand { get; set; } = "/ls";

	// The Shell to launch per command window: its executable, its .dll, or
	// its .csproj (run with "dotnet run" - slower to start). Null means
	// the path baked in from Directory.Build.props.
	public string? ShellPath { get; set; }

	public string? EffectiveShellPath =>
		ShellPath is { Length: > 0 } configured ? configured : BuildDefaults.ShellProject;

	public GuiSettings Normalize() {
		Theme = Theme is "light" ? "light" : "dark";
		FontSize = Math.Clamp(FontSize, 8, 32);
		MaxOutputLines = Math.Clamp(MaxOutputLines, 100, 100_000);
		HistorySize = Math.Clamp(HistorySize, 0, 10_000);

		if (string.IsNullOrWhiteSpace(FontFamily)) {
			FontFamily = new GuiSettings().FontFamily;
		}

		return this;
	}
}

// Everything restored between runs. Saved on exit, and from File > Save layout.
internal sealed class GuiState {
	public WindowPlacement Window { get; set; } = new();

	// The docking arrangement: which panels sit where, by ContentId.
	public DockLayoutState? Layout { get; set; }

	// One entry per open panel; ContentId ties it to its spot in Layout.
	public List<PanelState> Panels { get; set; } = [];

	public List<string> Bookmarks { get; set; } = [];

	public string? ExplorerPath { get; set; }
}

internal sealed class WindowPlacement {
	public double Width { get; set; } = 1280;
	public double Height { get; set; } = 800;
}

internal sealed class PanelState {
	public string ContentId { get; set; } = "";
	public string Kind { get; set; } = "";
	public string? Title { get; set; }

	// Command windows: where the session was, and what was typed.
	public string? Folder { get; set; }
	public string? Layer { get; set; }
	public List<string> History { get; set; } = [];

	// Service views: which view, and which command window's Shell owns it.
	public string? ViewId { get; set; }
	public string? OwnerId { get; set; }
}

// Serialized form of the docking tree (see Gui.Docking.cs).
internal sealed class DockLayoutState {
	public DockNodeState? Root { get; set; }
	public List<FloatingState> Floating { get; set; } = [];
}

internal sealed class DockNodeState {
	// "split" or "tabs".
	public string Kind { get; set; } = "tabs";

	// Splits.
	public bool Horizontal { get; set; }
	public List<double> Sizes { get; set; } = [];
	public List<DockNodeState> Children { get; set; } = [];

	// Tab groups: panel ContentIds in tab order.
	public List<string> Panels { get; set; } = [];
	public int Selected { get; set; }
}

internal sealed class FloatingState {
	public DockNodeState? Root { get; set; }
	public double Width { get; set; } = 640;
	public double Height { get; set; } = 480;
}

internal static class GuiStore {
	// %AppData%\Alexander\Gui on Windows, ~/.config/Alexander/Gui on Linux,
	// ~/Library/Application Support/Alexander/Gui on macOS.
	public static readonly string Folder = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
		"Alexander",
		"Gui");

	public static string SettingsFile => Path.Combine(Folder, "settings.json");
	public static string StateFile => Path.Combine(Folder, "state.json");

	private static readonly JsonSerializerOptions Json = new() {
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	// Startup only, before any window exists - nothing to block yet.
	public static GuiSettings LoadSettings() {
		return Load<GuiSettings>(SettingsFile).Normalize();
	}

	public static GuiState LoadState() {
		return Load<GuiState>(StateFile);
	}

	public static Task SaveSettingsAsync(GuiSettings settings) {
		return SaveAsync(SettingsFile, JsonSerializer.Serialize(settings, Json));
	}

	// Serialized now, on the caller's thread, so later changes to the live
	// object can't race the write.
	public static Task SaveStateAsync(GuiState state) {
		return SaveAsync(StateFile, JsonSerializer.Serialize(state, Json));
	}

	public static void EnsureFolder() {
		Directory.CreateDirectory(Folder);
	}

	private static T Load<T>(string path) where T : new() {
		try {
			return File.Exists(path)
				? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? new T()
				: new T();
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) {
			// A damaged file shouldn't stop the GUI from starting.
			return new T();
		}
	}

	// Write-then-rename, so a crash mid-save never leaves a half file.
	private static async Task SaveAsync(string path, string text) {
		await Task.Run(EnsureFolder);
		var temporary = path + ".tmp";
		await File.WriteAllTextAsync(temporary, text);
		File.Move(temporary, path, overwrite: true);
	}
}

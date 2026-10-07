using Microsoft.UI.Xaml;

using G = Alexander.Graphics;

namespace Alexander.Gui;

// A dockable panel: command window, folder explorer, settings, or a
// service view.
internal interface IPanel : IAsyncDisposable {
	// Stable id: ties the panel to its place in the dock layout and its PanelState.
	string ContentId { get; }

	string Title { get; }

	FrameworkElement Element { get; }

	// Tool panels (explorer, settings) open at the sides; documents
	// (command windows, service views) open in the main area.
	bool IsTool { get; }

	PanelState Capture();

	void ApplySettings(GuiSettings settings, GuiTheme theme);
}

// What panels can ask of the main window, so they don't depend on it
// (or on each other) directly.
internal interface IWorkbench {
	GuiSettings Settings { get; }
	GuiTheme Theme { get; }
	GuiState State { get; }

	// Fonts for service views and command windows, and their measurements
	// for the Shell.
	FontSet Fonts { get; }
	G.TextMetrics Metrics { get; }

	IReadOnlyList<CommandPanel> CommandPanels { get; }

	// The most recently focused command window, if any is open.
	CommandPanel? ActiveCommandPanel { get; }

	// Command windows opened, closed, focused or retitled.
	event Action? CommandPanelsChanged;

	CommandPanel OpenCommandWindow(string? folder = null);
	void OpenServiceView(CommandPanel owner, string viewId, string? title);

	// A command window's Shell exited or was restarted: its views go too.
	void CloseViewsOf(CommandPanel owner);

	void Activate(IPanel panel);
	void ClosePanel(IPanel panel);
	void PanelTitleChanged(IPanel panel);
	void PanelFocused(IPanel panel);

	Task SaveSettingsAsync(GuiSettings settings);
	void ResetLayoutOnNextStart();
}

internal static class CommandText {
	// Quotes a path for a --flag="..." value. On Windows the command
	// tokenizer treats \" as an escaped quote, so a trailing backslash
	// ("C:\") would swallow the closing quote - "C:\." is the same folder.
	public static string Quote(string path) {
		return path.EndsWith('\\') ? $"\"{path}.\"" : $"\"{path}\"";
	}
}

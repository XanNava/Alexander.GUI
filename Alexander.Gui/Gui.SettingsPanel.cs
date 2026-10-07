using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Alexander.Gui;

// Edits GuiSettings. Save applies immediately to every open panel (and
// every Shell, for service views) and writes settings.json.
internal sealed class SettingsPanel : UserControl, IPanel {
	private readonly IWorkbench workbench;
	private readonly ComboBox theme = new() { ItemsSource = new[] { "dark", "light" }, HorizontalAlignment = HorizontalAlignment.Stretch };
	private readonly TextBox fontFamily = new();
	private readonly TextBox fontSize = new();
	private readonly TextBox maxOutputLines = new();
	private readonly TextBox historySize = new();
	private readonly TextBox startFolder = new();
	private readonly TextBox explorerCommand = new();
	private readonly TextBox shellPath = new();
	private readonly CheckBox restoreLayout = new() { Content = "Reopen panels and layout on start" };
	private readonly CheckBox showHidden = new() { Content = "Show hidden folders" };
	private readonly Button saveButton = new() { Content = "Save" };
	private readonly TextBlock status = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
	private readonly Grid grid = new() { Margin = new Thickness(12), ColumnSpacing = 12, RowSpacing = 6 };
	private readonly ScrollViewer scroller = new();

	public SettingsPanel(IWorkbench workbench, PanelState? restore) {
		this.workbench = workbench;
		ContentId = restore?.ContentId ?? $"{PanelKinds.Settings}:{Guid.NewGuid():N}";

		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

		AddRow("Theme", theme);
		AddRow("Fonts", fontFamily);
		AddHint("Monospace preferences, first installed one is used.");
		AddRow("Font size", fontSize);
		AddRow("Output lines kept", maxOutputLines);
		AddRow("History size", historySize);
		AddRow("Start folder", startFolder);
		AddRow("Explorer run command", explorerCommand);
		AddRow("", restoreLayout);
		AddRow("", showHidden);

		AddHeading("Shell");
		AddRow("Shell", shellPath);
		AddHint("The Shell executable, its .dll, or its .csproj (started with \"dotnet run\" - slower). " +
			"Empty uses the default from Directory.Build.props. Applies to command windows opened or " +
			"restarted after saving. The Shell builds and loads Core itself.");

		saveButton.Click += (_, _) => SaveAsync().Forget("Saving settings");

		var resetLayout = new Button { Content = "Reset layout", Margin = new Thickness(6, 0, 0, 0) };
		ToolTipService.SetToolTip(resetLayout, "Start with the default panel arrangement next time");
		resetLayout.Click += (_, _) => {
			workbench.ResetLayoutOnNextStart();
			status.Text = "The default layout will be used next time Alexander starts.";
		};

		var openFolder = new Button { Content = "Open settings folder", Margin = new Thickness(6, 0, 0, 0) };
		openFolder.Click += (_, _) => Task.Run(() => {
			GuiStore.EnsureFolder();
			PlatformShell.RevealFolder(GuiStore.Folder);
		}).Forget("Opening the settings folder");

		var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
		buttons.Children.Add(saveButton);
		buttons.Children.Add(resetLayout);
		buttons.Children.Add(openFolder);
		AddRow("", buttons);
		AddRow("", status);

		scroller.Content = grid;
		Content = scroller;

		Load(workbench.Settings);
		ApplySettings(workbench.Settings, workbench.Theme);

		GotFocus += (_, _) => workbench.PanelFocused(this);
	}

	public string ContentId { get; }

	public string Title => "Settings";

	public FrameworkElement Element => this;

	public bool IsTool => true;

	public PanelState Capture() {
		return new PanelState { ContentId = ContentId, Kind = PanelKinds.Settings, Title = Title };
	}

	public void ApplySettings(GuiSettings settings, GuiTheme guiTheme) {
		RequestedTheme = guiTheme.ElementTheme;
		scroller.Background = guiTheme.Background;
		status.Foreground = guiTheme.Muted;
	}

	public ValueTask DisposeAsync() {
		return ValueTask.CompletedTask;
	}

	private void AddHeading(string text) {
		var row = NextRow();
		var heading = new TextBlock {
			Text = text,
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
			Margin = new Thickness(0, 14, 0, 0)
		};

		Grid.SetRow(heading, row);
		Grid.SetColumnSpan(heading, 2);
		grid.Children.Add(heading);
	}

	private void AddHint(string text) {
		AddRow("", new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
	}

	private void AddRow(string label, FrameworkElement control) {
		var row = NextRow();

		if (label.Length > 0) {
			var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
			Grid.SetRow(text, row);
			grid.Children.Add(text);
		}

		Grid.SetRow(control, row);
		Grid.SetColumn(control, 1);
		grid.Children.Add(control);
	}

	private int NextRow() {
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		return grid.RowDefinitions.Count - 1;
	}

	private void Load(GuiSettings settings) {
		theme.SelectedItem = settings.Theme;
		fontFamily.Text = settings.FontFamily;
		fontSize.Text = settings.FontSize.ToString(System.Globalization.CultureInfo.CurrentCulture);
		maxOutputLines.Text = settings.MaxOutputLines.ToString(System.Globalization.CultureInfo.CurrentCulture);
		historySize.Text = settings.HistorySize.ToString(System.Globalization.CultureInfo.CurrentCulture);
		startFolder.Text = settings.StartFolder ?? "";
		explorerCommand.Text = settings.ExplorerCommand;
		shellPath.Text = settings.ShellPath ?? "";
		shellPath.PlaceholderText = BuildDefaults.ShellProject ?? "(no default)";
		restoreLayout.IsChecked = settings.RestoreLayout;
		showHidden.IsChecked = settings.ShowHiddenFolders;
	}

	private async Task SaveAsync() {
		if (!double.TryParse(fontSize.Text, out var size)
			|| !int.TryParse(maxOutputLines.Text, out var lines)
			|| !int.TryParse(historySize.Text, out var history)) {

			status.Text = "Font size, output lines and history size must be numbers.";
			return;
		}

		var folder = startFolder.Text.Trim().Trim('"');
		var shell = shellPath.Text.Trim().Trim('"');

		saveButton.IsEnabled = false;
		status.Text = "Checking…";

		try {
			// Path checks off the UI thread (slow or network drives).
			var (folderOk, shellOk) = await Task.Run(() => (
				folder.Length == 0 || Directory.Exists(folder),
				shell.Length == 0 || File.Exists(shell)));

			if (!folderOk) {
				status.Text = $"Start folder doesn't exist: {folder}";
				return;
			}

			if (!shellOk) {
				status.Text = $"Shell not found: {shell}";
				return;
			}

			var updated = new GuiSettings {
				Theme = theme.SelectedItem as string ?? "dark",
				FontFamily = fontFamily.Text.Trim(),
				FontSize = size,
				MaxOutputLines = lines,
				HistorySize = history,
				StartFolder = folder.Length > 0 ? folder : null,
				ExplorerCommand = explorerCommand.Text.Trim(),
				RestoreLayout = restoreLayout.IsChecked == true,
				ShowHiddenFolders = showHidden.IsChecked == true,
				ShellPath = shell.Length > 0 ? shell : null
			}.Normalize();

			await workbench.SaveSettingsAsync(updated);
			Load(updated);
			status.Text = $"Saved to {GuiStore.SettingsFile}.";
		}
		catch (Exception exception) {
			status.Text = $"Couldn't save: {exception.Message}";
		}
		finally {
			saveButton.IsEnabled = true;
		}
	}
}

using System.Collections.ObjectModel;

using Alexander.Hosting;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

using Windows.ApplicationModel.DataTransfer;
using Windows.System;

using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace Alexander.Gui;

// One line of command-window output. Immutable: a theme change replaces
// the lines rather than notifying.
public sealed class OutputLine(string text, Brush brush, Windows.UI.Text.FontWeight weight) {
	public string Text { get; } = text;
	public Brush Brush { get; } = brush;
	public Windows.UI.Text.FontWeight Weight { get; } = weight;
}

// A command window: the front end for one Shell process. The Shell does
// the work - builds and loads Core, runs commands, handles /rebuild - and
// this shows its progress and output and sends it what you type. Folders
// can be sent or dropped in, and the Shell's service views opened from
// the Views button.
//
// Never blocks the UI thread: every Shell interaction is awaited, and
// output is appended in batches at low priority so a flood of lines (a
// build log) can't starve typing.
internal sealed class CommandPanel : UserControl, IPanel {
	private enum LineKind { Output, Error, Echo, Notice }

	private const string LineTemplate = """
		<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
			<TextBlock Text="{Binding Text}" Foreground="{Binding Brush}" FontWeight="{Binding Weight}"
				TextWrapping="Wrap" IsTextSelectionEnabled="True" />
		</DataTemplate>
		""";

	// Terminal-tight rows instead of the default touch-friendly ListView items.
	private const string LineStyle = """
		<Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListViewItem">
			<Setter Property="MinHeight" Value="0" />
			<Setter Property="Padding" Value="8,0,8,0" />
			<Setter Property="Margin" Value="0" />
			<Setter Property="HorizontalContentAlignment" Value="Stretch" />
		</Style>
		""";

	private readonly IWorkbench workbench;
	private readonly Grid headerBar = new();
	private readonly Grid inputBar = new();
	private readonly TextBlock header = new();
	private readonly Button viewsButton = new();
	private readonly Button restartButton = new();
	private readonly ListView output = new();
	private readonly ObservableCollection<OutputLine> lines = [];
	private readonly List<(string Text, LineKind Kind)> lineKinds = [];
	private readonly TextBlock prompt = new();
	private readonly TextBox input = new();

	private readonly List<string> history;
	private int historyIndex;

	private IReadOnlyList<string> completions = [];
	private int completionIndex = -1;
	private int completionRequest;
	private bool applyingCompletion;

	private readonly List<(string Text, LineKind Kind)> pendingLines = [];
	private bool flushScheduled;

	private readonly string launchFolder;
	private string? restoreFolder;
	private string? restoreLayer;
	private string status = "Starting the Shell…";
	private int running;
	private bool disposed;

	public CommandPanel(IWorkbench workbench, PanelState? restore, string? folder) {
		this.workbench = workbench;

		ContentId = restore?.ContentId ?? $"{PanelKinds.Command}:{Guid.NewGuid():N}";
		history = restore?.History.ToList() ?? [];
		historyIndex = history.Count;
		restoreFolder = folder ?? restore?.Folder;
		restoreLayer = restore?.Layer;

		launchFolder = workbench.Settings.StartFolder is { } start && Directory.Exists(start)
			? start
			: Environment.CurrentDirectory;

		Session = CreateSession();

		BuildLayout();
		ApplySettings(workbench.Settings, workbench.Theme);
		UpdateHeader();

		// Docking re-parents the panel; land back at the newest output.
		Loaded += (_, _) => {
			ScrollToEnd();
			input.Focus(FocusState.Programmatic);
		};

		GotFocus += (_, _) => workbench.PanelFocused(this);
	}

	public ShellSession Session { get; private set; }

	public string ContentId { get; }

	public FrameworkElement Element => this;

	public bool IsTool => false;

	public string Title {
		get {
			var name = Session.State?.Prompt is { Length: > 0 } label ? label : "Command";
			return running > 0 || !Session.IsReady ? $"{name} …" : name;
		}
	}

	// Completes once the Shell is ready (or failed) and the window's
	// folder/layer have been restored.
	public Task Ready { get; private set; } = Task.CompletedTask;

	public void Start() {
		Ready = StartAsync();
		Ready.Forget("Starting a command window");
	}

	// Runs a command as if typed (echoed, added to the output). The Shell
	// runs one command at a time, so these queue there, not here.
	public async Task RunAsync(string text, bool echo = true) {
		if (echo) {
			Append($"{Session.State?.Prompt ?? ""}> {text}", LineKind.Echo);
		}

		if (!Session.IsReady) {
			Append(
				Session.IsRunning ? "The Shell is still starting - try again in a moment." : "The Shell isn't running. Use Restart.",
				LineKind.Notice);

			return;
		}

		running++;
		UpdateHeader();

		try {
			var result = await Session.RunAsync(text);

			if (!string.IsNullOrEmpty(result.Message)) {
				var isError = result.Failed || result.Message.StartsWith("Error:", StringComparison.Ordinal);
				Append(result.Message, isError ? LineKind.Error : LineKind.Notice);
			}

			// "rebuild" needs nothing here: the Shell rebuilds and reports
			// progress like it does at startup.
			if (result.Action == "exit") {
				workbench.ClosePanel(this);
			}
		}
		catch (OperationCanceledException) {
			// Closing.
		}
		catch (Exception exception) {
			Append($"Error: {exception.Message}", LineKind.Error);
		}
		finally {
			running--;
			UpdateHeader();
		}
	}

	// What the folder explorer (and drag-and-drop) calls.
	public Task SetFolderAsync(string path) {
		return RunAsync($"/cd --path={CommandText.Quote(path)}");
	}

	public async Task RunInFolderAsync(string path, string command) {
		await SetFolderAsync(path);
		await RunAsync(command);
	}

	// Lines from outside the Shell (status messages from the GUI itself).
	public void Report(string text, bool isError = false) {
		Append(text, isError ? LineKind.Error : LineKind.Notice);
	}

	// Stops this window's Shell (if still running) and starts a fresh one
	// in the same folder and layer.
	public async Task RestartAsync() {
		restoreFolder = Session.State?.Folder is { Length: > 0 } folder ? folder : restoreFolder;
		restoreLayer = Session.State?.Layer ?? restoreLayer;

		workbench.CloseViewsOf(this);

		var old = Session;
		Session = CreateSession();
		status = "Restarting the Shell…";
		UpdateHeader();

		await old.DisposeAsync();
		Append("— restarted —", LineKind.Notice);
		Start();
	}

	public PanelState Capture() {
		var state = Session.State;

		return new PanelState {
			ContentId = ContentId,
			Kind = PanelKinds.Command,
			Title = Title,
			Folder = state?.Folder is { Length: > 0 } folder ? folder : restoreFolder,
			Layer = state is not null ? state.Layer : restoreLayer,
			History = history.TakeLast(workbench.Settings.HistorySize).ToList()
		};
	}

	public void ApplySettings(GuiSettings settings, GuiTheme theme) {
		var font = new FontFamily(workbench.Fonts.MonoFamily);

		RequestedTheme = theme.ElementTheme;
		Background = theme.Background;
		headerBar.Background = theme.Surface;
		inputBar.Background = theme.Surface;
		header.Foreground = theme.Muted;
		prompt.Foreground = theme.Accent;

		output.Background = theme.Background;
		output.FontFamily = font;
		output.FontSize = settings.FontSize;
		prompt.FontFamily = font;
		prompt.FontSize = settings.FontSize;
		input.FontFamily = font;
		input.FontSize = settings.FontSize;

		// Recolour existing output for the new theme.
		lines.Clear();

		foreach (var (text, kind) in lineKinds) {
			lines.Add(MakeLine(text, kind));
		}

		// The Shell lays service views out with these.
		Session.SendInitAsync(workbench.Metrics, settings.Theme).Forget("Updating the Shell's theme");
	}

	public async ValueTask DisposeAsync() {
		disposed = true;
		await Session.DisposeAsync();
	}

	// --- session ---------------------------------------------------------

	private ShellSession CreateSession() {
		var session = new ShellSession();

		// Handlers ignore a session that's been replaced by a restart.
		session.Output += (text, isError) => {
			if (session == Session) {
				Append(text, isError ? LineKind.Error : LineKind.Output);
			}
		};

		session.Progress += (stage, text, isError) => {
			if (session != Session) {
				return;
			}

			if (!string.IsNullOrEmpty(text)) {
				Append(text, isError ? LineKind.Error : LineKind.Notice);
			}

			status = stage switch {
				HostStages.Building => "Building Core…",
				HostStages.Loading => "Loading Core…",
				HostStages.Failed => "Core failed - see above",
				HostStages.Ready => "",
				_ => status
			};

			UpdateHeader();
		};

		session.Echo += text => {
			if (session == Session) {
				Append($"{session.State?.Prompt ?? ""}> {text}", LineKind.Echo);
			}
		};

		session.StateChanged += _ => {
			if (session == Session) {
				UpdateHeader();
			}
		};

		session.Exited += code => {
			if (session != Session || disposed) {
				return;
			}

			Append($"The Shell exited{(code is { } exitCode ? $" (code {exitCode})" : "")}. Use Restart to start a new one.", LineKind.Error);
			status = "Shell stopped";
			workbench.CloseViewsOf(this);
			UpdateHeader();
		};

		return session;
	}

	private async Task StartAsync() {
		var session = Session;
		status = "Starting the Shell…";
		UpdateHeader();

		if (workbench.Settings.EffectiveShellPath is not { } shellPath) {
			Append("No Shell is configured. Set its path in Settings.", LineKind.Error);
			status = "No Shell configured";
			UpdateHeader();
			return;
		}

		try {
			await session.StartAsync(shellPath, launchFolder, workbench.Metrics, workbench.Settings.Theme);
		}
		catch (Exception exception) {
			Append(exception.Message, LineKind.Error);
			status = "Shell didn't start";
			UpdateHeader();
			return;
		}

		if (session != Session || !session.IsReady) {
			UpdateHeader();
			return;
		}

		// Put the session back where it was (or where it was sent).
		if (restoreFolder is { } folder && await Task.Run(() => Directory.Exists(folder))) {
			await RunAsync($"/cd --path={CommandText.Quote(folder)}", echo: false);
		}

		if (!string.IsNullOrEmpty(restoreLayer)) {
			await RunAsync($"/{restoreLayer}", echo: false);
		}

		UpdateHeader();
	}

	// --- layout ----------------------------------------------------------

	private void BuildLayout() {
		header.Margin = new Thickness(10, 6, 10, 6);
		header.VerticalAlignment = VerticalAlignment.Center;
		header.TextTrimming = TextTrimming.CharacterEllipsis;

		restartButton.Content = "Restart";
		restartButton.Margin = new Thickness(4, 3, 0, 3);
		ToolTipService.SetToolTip(restartButton, "Stop this window's Shell and start a new one");
		restartButton.Click += (_, _) => RestartAsync().Forget("Restarting the Shell");

		viewsButton.Content = "Views ▾";
		viewsButton.Margin = new Thickness(4, 3, 6, 3);
		ToolTipService.SetToolTip(viewsButton, "Open one of this Shell's service views");
		viewsButton.Click += (_, _) => ShowViewsMenu();

		headerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		headerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		headerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		Grid.SetColumn(restartButton, 1);
		Grid.SetColumn(viewsButton, 2);
		headerBar.Children.Add(header);
		headerBar.Children.Add(restartButton);
		headerBar.Children.Add(viewsButton);

		output.ItemsSource = lines;
		output.ItemTemplate = (DataTemplate)XamlReader.Load(LineTemplate);
		output.ItemContainerStyle = (Style)XamlReader.Load(LineStyle);
		output.SelectionMode = ListViewSelectionMode.None;
		output.IsItemClickEnabled = false;
		output.ContextFlyout = BuildOutputMenu();

		prompt.Margin = new Thickness(10, 0, 4, 0);
		prompt.VerticalAlignment = VerticalAlignment.Center;

		input.BorderThickness = new Thickness(0);
		input.Background = workbench.Theme.Transparent;
		input.PlaceholderText = "Type a command (Tab completes, ↑/↓ history)";
		input.PreviewKeyDown += OnInputKeyDown;
		input.TextChanged += (_, _) => {
			if (!applyingCompletion) {
				completionIndex = -1;
			}
		};

		inputBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		inputBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		Grid.SetColumn(input, 1);
		inputBar.Children.Add(prompt);
		inputBar.Children.Add(input);

		var root = new Grid();
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		Grid.SetRow(output, 1);
		Grid.SetRow(inputBar, 2);
		root.Children.Add(headerBar);
		root.Children.Add(output);
		root.Children.Add(inputBar);
		Content = root;

		// A folder dropped anywhere on the window becomes a /cd.
		AllowDrop = true;
		DragOver += OnDragOver;
		Drop += OnDrop;
	}

	private MenuFlyout BuildOutputMenu() {
		var menu = new MenuFlyout();

		var copy = new MenuFlyoutItem { Text = "Copy all output" };
		copy.Click += (_, _) => {
			var package = new DataPackage();
			package.SetText(string.Join(Environment.NewLine, lineKinds.Select(line => line.Text)));
			Clipboard.SetContent(package);
		};

		var clear = new MenuFlyoutItem { Text = "Clear" };
		clear.Click += (_, _) => {
			lines.Clear();
			lineKinds.Clear();
		};

		menu.Items.Add(copy);
		menu.Items.Add(clear);
		return menu;
	}

	private void UpdateHeader() {
		var state = Session.State;
		var folder = state?.Folder is { Length: > 0 } current ? current : restoreFolder ?? launchFolder;

		var parts = new List<string> { folder };

		if (state?.Layer is { } layer) {
			parts.Add($"{layer} layer (/exit to leave)");
		}

		if (status.Length > 0) {
			parts.Add(status);
		}

		header.Text = string.Join("   ·   ", parts);
		prompt.Text = $"{state?.Prompt ?? ""}>";
		restartButton.Visibility = Session.IsRunning && Session.IsReady && status.Length == 0
			? Visibility.Collapsed
			: Visibility.Visible;

		workbench.PanelTitleChanged(this);
	}

	// Lines arrive one event at a time; they're added in batches at low
	// priority, so input and rendering always get a turn.
	private void Append(string text, LineKind kind) {
		pendingLines.Add((text, kind));

		if (!flushScheduled) {
			flushScheduled = true;
			UiThread.Post(FlushLines, DispatcherQueuePriority.Low);
		}
	}

	private void FlushLines() {
		flushScheduled = false;

		if (pendingLines.Count == 0) {
			return;
		}

		var limit = workbench.Settings.MaxOutputLines;

		// Lines that would be trimmed straight away aren't worth adding.
		foreach (var (text, kind) in pendingLines.Skip(Math.Max(0, pendingLines.Count - limit))) {
			lineKinds.Add((text, kind));
			lines.Add(MakeLine(text, kind));
		}

		pendingLines.Clear();

		var excess = lines.Count - limit;

		if (excess > 0) {
			lineKinds.RemoveRange(0, excess);

			for (var index = 0; index < excess; index++) {
				lines.RemoveAt(0);
			}
		}

		ScrollToEnd();
	}

	private void ScrollToEnd() {
		if (lines.Count > 0) {
			output.ScrollIntoView(lines[^1]);
		}
	}

	private OutputLine MakeLine(string text, LineKind kind) {
		var theme = workbench.Theme;

		var brush = kind switch {
			LineKind.Error => theme.Danger,
			LineKind.Echo => theme.Accent,
			LineKind.Notice => theme.Muted,
			_ => theme.Text
		};

		return new OutputLine(text, brush, kind == LineKind.Echo ? FontWeights.SemiBold : FontWeights.Normal);
	}

	// --- input -----------------------------------------------------------

	private void OnInputKeyDown(object sender, KeyRoutedEventArgs e) {
		switch (e.Key) {
			case VirtualKey.Enter:
				Submit();
				e.Handled = true;
				break;

			case VirtualKey.Up:
				if (historyIndex > 0) {
					historyIndex--;
					SetInput(history[historyIndex]);
				}

				e.Handled = true;
				break;

			case VirtualKey.Down:
				if (historyIndex < history.Count - 1) {
					historyIndex++;
					SetInput(history[historyIndex]);
				} else {
					historyIndex = history.Count;
					SetInput("");
				}

				e.Handled = true;
				break;

			case VirtualKey.Tab:
				CompleteAsync(backwards: shiftDown).Forget("Completing");
				e.Handled = true;
				break;

			case VirtualKey.Escape:
				SetInput("");
				e.Handled = true;
				break;
		}
	}

	private bool shiftDown;

	protected override void OnKeyDown(KeyRoutedEventArgs e) {
		if (e.Key == VirtualKey.Shift) {
			shiftDown = true;
		}

		base.OnKeyDown(e);
	}

	protected override void OnKeyUp(KeyRoutedEventArgs e) {
		if (e.Key == VirtualKey.Shift) {
			shiftDown = false;
		}

		base.OnKeyUp(e);
	}

	private void Submit() {
		var text = input.Text.Trim();

		if (text.Length == 0) {
			return;
		}

		if (history.Count == 0 || history[^1] != text) {
			history.Add(text);

			if (history.Count > workbench.Settings.HistorySize) {
				history.RemoveRange(0, history.Count - workbench.Settings.HistorySize);
			}
		}

		historyIndex = history.Count;
		SetInput("");
		RunAsync(text).Forget("Running a command");
	}

	// Tab cycles through the Shell's completions (Shift+Tab backwards);
	// typing anything else starts over. The first Tab asks the Shell
	// asynchronously, and the answer is dropped if you've typed since.
	private async Task CompleteAsync(bool backwards) {
		if (completionIndex < 0) {
			var typed = input.Text;
			var request = ++completionRequest;
			var items = await Session.CompleteAsync(typed);

			if (request != completionRequest || input.Text != typed || items.Count == 0) {
				return;
			}

			completions = items;
			completionIndex = backwards ? items.Count - 1 : 0;
		} else {
			completionIndex = (completionIndex + (backwards ? -1 : 1) + completions.Count) % completions.Count;
		}

		applyingCompletion = true;
		SetInput(completions[completionIndex]);
		applyingCompletion = false;
	}

	private void SetInput(string text) {
		input.Text = text;
		input.SelectionStart = text.Length;
	}

	// --- views and drag-and-drop ----------------------------------------

	private void ShowViewsMenu() {
		var menu = new MenuFlyout();
		var views = Session.IsReady ? Session.State?.Views ?? [] : [];

		if (views.Count == 0) {
			menu.Items.Add(new MenuFlyoutItem {
				Text = Session.IsReady ? "No service views" : "Waiting for the Shell…",
				IsEnabled = false
			});
		}

		foreach (var view in views) {
			var item = new MenuFlyoutItem { Text = view.Title };
			ToolTipService.SetToolTip(item, view.Description);
			item.Click += (_, _) => workbench.OpenServiceView(this, view.Id, view.Title);
			menu.Items.Add(item);
		}

		menu.ShowAt(viewsButton);
	}

	private void OnDragOver(object sender, DragEventArgs e) {
		if (e.DataView.Contains(StandardDataFormats.Text) || e.DataView.Contains(StandardDataFormats.StorageItems)) {
			e.AcceptedOperation = DataPackageOperation.Copy;
			e.Handled = true;
		}
	}

	private async void OnDrop(object sender, DragEventArgs e) {
		var deferral = e.GetDeferral();
		string? path = null;

		try {
			if (e.DataView.Contains(StandardDataFormats.StorageItems)) {
				var items = await e.DataView.GetStorageItemsAsync();
				path = items.FirstOrDefault()?.Path;
			} else if (e.DataView.Contains(StandardDataFormats.Text)) {
				path = (await e.DataView.GetTextAsync()).Trim();
			}
		}
		catch (Exception exception) {
			Append($"Error: couldn't read the dropped item ({exception.Message})", LineKind.Error);
		}
		finally {
			deferral.Complete();
		}

		if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) {
			return;
		}

		try {
			// A file means its folder. Checked off the UI thread: the
			// path may be on a slow or disconnected drive.
			var folder = await Task.Run(() =>
				File.Exists(path) ? Path.GetDirectoryName(path)
				: Directory.Exists(path) ? path
				: null);

			if (folder is not null) {
				await SetFolderAsync(folder);
			}
		}
		catch (Exception exception) {
			Append($"Error: {exception.Message}", LineKind.Error);
		}
	}
}

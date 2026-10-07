using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Graphics;

using G = Alexander.Graphics;

namespace Alexander.Gui;

// The main window: a menu bar over the dock area. Every panel can be
// dragged to any edge of any group, tabbed with others, floated into a
// window of its own, or resized with the bars between groups. Command
// windows and service views open as documents; the folder explorer and
// settings open as tool panels at the sides.
//
// Each command window is backed by its own Shell process (ShellSession),
// which owns Core entirely - building, loading, /rebuild. The GUI only
// displays and forwards, and never blocks its UI thread on any of it.
internal sealed class MainWindow : IWorkbench {
	private readonly Window window = new() { Title = "Alexander" };
	private readonly Grid root = new();
	private readonly DockManager dock;
	private readonly MenuFlyoutSubItem serviceViewsMenu = new() { Text = "Service views" };

	private CommandPanel? lastActive;
	private bool layoutBuilt;
	private bool resetLayout;
	private bool shuttingDown;
	private bool readyToClose;

	public MainWindow(GuiSettings settings, GuiState state) {
		Settings = settings;
		State = state;
		Theme = GuiTheme.From(settings);
		Fonts = new FontSet(settings.FontFamily);
		Metrics = Fonts.CreateMetrics();

		dock = new DockManager(this, window);
		dock.PanelClosed += OnPanelClosed;
		dock.PanelFocused += PanelFocused;

		var menu = BuildMenu();
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		Grid.SetRow(dock.Main, 1);
		root.Children.Add(menu);
		root.Children.Add(dock.Main);
		window.Content = root;

		ApplyTheme();
		Diagnostics.DialogRoot = () => root.XamlRoot;

		root.Loaded += (_, _) => {
			if (!layoutBuilt) {
				layoutBuilt = true;
				BuildLayout();
			}
		};

		// Closing waits for every Shell to stop cleanly - without blocking.
		try {
			window.AppWindow.Closing += (_, e) => {
				if (readyToClose) {
					return;
				}

				e.Cancel = true;
				ShutdownAsync().Forget("Closing Alexander");
			};
		}
		catch {
			// No AppWindow on this host: the Closed fallback below applies.
		}

		// Fallback where Closing isn't raised: save what we can. Shells
		// stop by themselves when the GUI's end of their pipe closes.
		window.Closed += (_, _) => {
			if (!shuttingDown) {
				SaveAllAsync().Forget("Saving on close");
			}
		};

		try {
			window.AppWindow.Resize(new SizeInt32 {
				Width = (int)state.Window.Width,
				Height = (int)state.Window.Height
			});
		}
		catch {
			// Not supported on every desktop host; the default size is fine.
		}
	}

	public GuiSettings Settings { get; private set; }

	public GuiTheme Theme { get; private set; }

	public GuiState State { get; }

	public FontSet Fonts { get; private set; }

	public G.TextMetrics Metrics { get; private set; }

	public event Action? CommandPanelsChanged;

	public IReadOnlyList<CommandPanel> CommandPanels => dock.Panels.OfType<CommandPanel>().ToList();

	public CommandPanel? ActiveCommandPanel =>
		lastActive is not null && CommandPanels.Contains(lastActive)
			? lastActive
			: CommandPanels.FirstOrDefault();

	public void Activate() {
		window.Activate();
	}

	// --- IWorkbench ------------------------------------------------------

	public CommandPanel OpenCommandWindow(string? folder = null) {
		var panel = new CommandPanel(this, restore: null, folder);
		dock.AddDocument(panel);
		panel.Start();
		RaiseCommandPanelsChanged();
		return panel;
	}

	public void OpenServiceView(CommandPanel owner, string viewId, string? title) {
		dock.AddDocument(new ServiceViewPanel(this, owner, viewId, title, contentId: null));
	}

	public void CloseViewsOf(CommandPanel owner) {
		foreach (var view in dock.Panels.OfType<ServiceViewPanel>().Where(view => view.Owner == owner).ToList()) {
			dock.Close(view);
		}
	}

	public void Activate(IPanel panel) {
		dock.Activate(panel);
	}

	public void ClosePanel(IPanel panel) {
		dock.Close(panel);
	}

	public void PanelTitleChanged(IPanel panel) {
		dock.UpdateTitle(panel);

		if (panel is CommandPanel) {
			RaiseCommandPanelsChanged();
		}
	}

	public void PanelFocused(IPanel panel) {
		if (panel is CommandPanel command && command != lastActive) {
			lastActive = command;
			RaiseCommandPanelsChanged();
		}
	}

	public async Task SaveSettingsAsync(GuiSettings settings) {
		if (settings.FontFamily != Settings.FontFamily) {
			Fonts = new FontSet(settings.FontFamily);
			Metrics = Fonts.CreateMetrics();
		}

		Settings = settings;
		Theme = GuiTheme.From(settings);
		ApplyTheme();
		dock.RenderAll();

		// Each command window also forwards the new theme and metrics to
		// its Shell, which re-renders its service views.
		foreach (var panel in dock.Panels.ToList()) {
			panel.ApplySettings(Settings, Theme);
		}

		await GuiStore.SaveSettingsAsync(settings);
	}

	public void ResetLayoutOnNextStart() {
		resetLayout = true;
	}

	// --- menu ------------------------------------------------------------

	private MenuBar BuildMenu() {
		var file = new MenuBarItem { Title = "File" };
		file.Items.Add(MenuEntry("New command window", () => OpenCommandWindow()));
		file.Items.Add(new MenuFlyoutSeparator());
		file.Items.Add(MenuEntry("Rebuild Core in this window", () => WithActive(panel => panel.RunAsync("/rebuild --confirm").Forget("Rebuilding Core"))));
		file.Items.Add(MenuEntry("Restart this window's Shell", () => WithActive(panel => panel.RestartAsync().Forget("Restarting the Shell"))));
		file.Items.Add(MenuEntry("Open console Shell", OpenConsoleShell));
		file.Items.Add(new MenuFlyoutSeparator());
		file.Items.Add(MenuEntry("Save layout now", () => SaveAllAsync().Forget("Saving the layout")));
		file.Items.Add(new MenuFlyoutSeparator());
		file.Items.Add(MenuEntry("Exit", () => ShutdownAsync().Forget("Closing Alexander")));

		var view = new MenuBarItem { Title = "View" };
		view.Items.Add(MenuEntry("Folder explorer", () => ShowTool(PanelKinds.Explorer)));
		view.Items.Add(serviceViewsMenu);
		view.Items.Add(new MenuFlyoutSeparator());
		view.Items.Add(MenuEntry("Settings", () => ShowTool(PanelKinds.Settings)));

		FillServiceViewsMenu();

		var menu = new MenuBar();
		menu.Items.Add(file);
		menu.Items.Add(view);
		return menu;
	}

	private static MenuFlyoutItem MenuEntry(string text, Action action) {
		var item = new MenuFlyoutItem { Text = text };
		item.Click += (_, _) => action();
		return item;
	}

	private void WithActive(Action<CommandPanel> action) {
		if (ActiveCommandPanel is { } panel) {
			action(panel);
		}
	}

	// Kept current as command windows come, go, get focus or change state,
	// since a sub-menu has no "about to open" moment to fill it in.
	private void FillServiceViewsMenu() {
		serviceViewsMenu.Items.Clear();

		if (ActiveCommandPanel is not { } owner) {
			serviceViewsMenu.Items.Add(new MenuFlyoutItem { Text = "Open a command window first", IsEnabled = false });
			return;
		}

		var views = owner.Session.IsReady ? owner.Session.State?.Views ?? [] : [];

		if (views.Count == 0) {
			serviceViewsMenu.Items.Add(new MenuFlyoutItem {
				Text = owner.Session.IsReady ? "No service views" : "Waiting for the Shell…",
				IsEnabled = false
			});
		}

		foreach (var info in views) {
			var item = MenuEntry(info.Title, () => OpenServiceView(owner, info.Id, info.Title));
			ToolTipService.SetToolTip(item, $"{info.Description} (Shell: {owner.Title})");
			serviceViewsMenu.Items.Add(item);
		}
	}

	private void RaiseCommandPanelsChanged() {
		FillServiceViewsMenu();
		CommandPanelsChanged?.Invoke();
	}

	// The real console Shell in its own terminal window (not host mode),
	// for when you want the terminal itself.
	private void OpenConsoleShell() {
		if (Settings.EffectiveShellPath is not { } shellPath) {
			Diagnostics.ShowAsync("Open console Shell", "No Shell is configured. Set its path in Settings.").Forget();
			return;
		}

		var folder = ActiveCommandPanel?.Session.State?.Folder is { Length: > 0 } current
			? current
			: Environment.CurrentDirectory;

		var (fileName, arguments) = Path.GetExtension(shellPath).ToLowerInvariant() switch {
			".csproj" => ("dotnet", new[] { "run", "--project", shellPath }),
			".dll" => ("dotnet", new[] { shellPath }),
			_ => (shellPath, Array.Empty<string>())
		};

		Task.Run(() => PlatformShell.OpenInTerminal(fileName, arguments, folder)).Forget("Opening the console Shell");
	}

	// --- layout ----------------------------------------------------------

	private void BuildLayout() {
		var restored = false;

		if (Settings.RestoreLayout && State.Layout is { } layout && State.Panels.Count > 0) {
			try {
				restored = RestoreLayout(layout);
			}
			catch (Exception exception) {
				Diagnostics.Report("Restoring the saved layout", exception);
			}
		}

		if (!restored) {
			dock.BuildDefault(new FolderExplorerPanel(this, restore: null));
		}

		if (CommandPanels.Count == 0) {
			OpenCommandWindow();
		}
	}

	private bool RestoreLayout(DockLayoutState layout) {
		var saved = State.Panels
			.Where(panel => panel.ContentId.Length > 0)
			.GroupBy(panel => panel.ContentId)
			.ToDictionary(group => group.Key, group => group.First());

		// Command windows first, so service views can attach to their
		// owner while the layout is rebuilt. (Their Shells start after.)
		var commands = saved.Values
			.Where(panel => panel.Kind == PanelKinds.Command)
			.Select(panel => new CommandPanel(this, panel, folder: null))
			.ToDictionary(panel => panel.ContentId);

		IPanel? Resolve(string id) {
			if (!saved.TryGetValue(id, out var panelState)) {
				return null;
			}

			return panelState.Kind switch {
				PanelKinds.Command => commands.GetValueOrDefault(id),
				PanelKinds.Explorer => new FolderExplorerPanel(this, panelState),
				PanelKinds.Settings => new SettingsPanel(this, panelState),

				// Opens once its owner's Shell is ready; closes itself if
				// that Shell no longer offers the view.
				PanelKinds.View when panelState.ViewId is { } viewId && commands.TryGetValue(panelState.OwnerId ?? "", out var owner) =>
					new ServiceViewPanel(this, owner, viewId, panelState.Title, panelState.ContentId),

				_ => null
			};
		}

		var placed = dock.Restore(layout, Resolve);

		// A command window that didn't make it into the layout still gets a tab.
		foreach (var command in commands.Values.Where(command => !placed.Contains(command))) {
			dock.AddDocument(command);
		}

		// Shells start one at a time (ShellSession queues them), so each
		// builds Core without competing with the others.
		foreach (var command in commands.Values) {
			command.Start();
		}

		RaiseCommandPanelsChanged();
		return dock.Panels.Any();
	}

	// The explorer and settings are single-instance tool panels.
	private void ShowTool(string kind) {
		var existing = dock.Panels.FirstOrDefault(panel => panel switch {
			FolderExplorerPanel => kind == PanelKinds.Explorer,
			SettingsPanel => kind == PanelKinds.Settings,
			_ => false
		});

		if (existing is not null) {
			dock.Activate(existing);
			return;
		}

		if (kind == PanelKinds.Explorer) {
			dock.AddTool(new FolderExplorerPanel(this, restore: null), DockZone.Left);
		} else {
			dock.AddTool(new SettingsPanel(this, restore: null), DockZone.Right);
		}
	}

	private void OnPanelClosed(IPanel panel) {
		if (panel is CommandPanel command) {
			if (lastActive == command) {
				lastActive = null;
			}

			// A view can't outlive the Shell it draws from.
			CloseViewsOf(command);
			RaiseCommandPanelsChanged();
		}

		// Stopping a Shell can take a moment; never wait for it here.
		panel.DisposeAsync().AsTask().Forget($"Closing '{panel.Title}'");
	}

	// --- theme and shutdown ----------------------------------------------

	private void ApplyTheme() {
		root.RequestedTheme = Theme.ElementTheme;
		root.Background = Theme.Background;
	}

	// Snapshots on the UI thread (it reads controls), writes asynchronously.
	private async Task SaveAllAsync() {
		var bounds = window.Bounds;

		if (bounds.Width > 0 && bounds.Height > 0) {
			State.Window = new WindowPlacement { Width = bounds.Width, Height = bounds.Height };
		}

		State.Panels = dock.Panels.Select(panel => panel.Capture()).ToList();
		State.Layout = resetLayout ? null : dock.Capture();

		await GuiStore.SaveStateAsync(State);
	}

	// Saves state, then asks every Shell to stop (each disposes Core and
	// its services cleanly) - views first, then all Shells in parallel -
	// and only then exits. The window stays responsive throughout.
	private async Task ShutdownAsync() {
		if (shuttingDown) {
			return;
		}

		shuttingDown = true;
		window.Title = "Alexander - closing…";

		try {
			await SaveAllAsync();
		}
		catch (Exception exception) {
			Diagnostics.Report("Saving the layout", exception, quiet: true);
		}

		var panels = dock.Panels.ToList();
		dock.CloseFloatingWindows();

		await DisposeAllAsync(panels.Where(panel => panel is not CommandPanel));
		await DisposeAllAsync(panels.OfType<CommandPanel>());

		readyToClose = true;
		Application.Current.Exit();
	}

	private static async Task DisposeAllAsync(IEnumerable<IPanel> panels) {
		await Task.WhenAll(panels.Select(async panel => {
			try {
				await panel.DisposeAsync();
			}
			catch (Exception exception) {
				Diagnostics.Report($"Closing '{panel.Title}'", exception, quiet: true);
			}
		}));
	}
}

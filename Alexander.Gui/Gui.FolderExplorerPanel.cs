using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Windows.ApplicationModel.DataTransfer;
using Windows.System;

using FileAttributes = System.IO.FileAttributes;

namespace Alexander.Gui;

// The folder explorer: not a file manager, but a picker whose job is to
// hand a folder to a command window. Pick a folder in the tree, pick a
// target ("Send to"), then either make it that Shell's current folder or
// run a command there. Folders can also be dragged onto any command
// window, and right-clicked for the same actions.
//
// Roots per OS: drives on Windows; "/" plus mounted volumes on macOS
// (/Volumes) and Linux (/media, /mnt, /run/media). All disk access runs
// off the UI thread - enumeration can stall on network or sleeping drives.
internal sealed class FolderExplorerPanel : UserControl, IPanel {
	// What each tree node holds. ToString is what the tree shows.
	private sealed class FolderEntry(string path, string label) {
		public string Path { get; } = path;
		public string Label { get; } = label;
		public Task? Loading { get; set; }
		public override string ToString() => Label;
	}

	private sealed record TargetChoice(string Label, CommandPanel? Panel) {
		public override string ToString() => Label;
	}

	private readonly IWorkbench workbench;
	private readonly TextBox pathBox = new();
	private readonly TreeView tree = new();
	private readonly ComboBox targets = new();
	private readonly TextBox commandBox = new();
	private readonly TextBlock status = new();
	private readonly Grid root = new();
	private readonly List<TreeViewNode> volumeRoots = [];

	private int rootsVersion;

	public FolderExplorerPanel(IWorkbench workbench, PanelState? restore) {
		this.workbench = workbench;
		ContentId = restore?.ContentId ?? $"{PanelKinds.Explorer}:{Guid.NewGuid():N}";

		BuildLayout();
		ApplyColors(workbench.Theme);
		commandBox.Text = workbench.Settings.ExplorerCommand;

		workbench.CommandPanelsChanged += RefreshTargets;
		RefreshTargets();

		GotFocus += (_, _) => workbench.PanelFocused(this);

		RebuildAndRevealAsync(workbench.State.ExplorerPath).Forget("Loading folders");
	}

	public string ContentId { get; }

	public string Title => "Folders";

	public FrameworkElement Element => this;

	public bool IsTool => true;

	private string? SelectedPath => (tree.SelectedNode?.Content as FolderEntry)?.Path;

	public PanelState Capture() {
		return new PanelState { ContentId = ContentId, Kind = PanelKinds.Explorer, Title = Title };
	}

	public void ApplySettings(GuiSettings settings, GuiTheme theme) {
		ApplyColors(theme);

		if (string.IsNullOrEmpty(commandBox.Text)) {
			commandBox.Text = settings.ExplorerCommand;
		}

		// Hidden-folder visibility may have changed: reload, keeping the selection.
		RebuildAndRevealAsync(SelectedPath ?? workbench.State.ExplorerPath).Forget("Reloading folders");
	}

	public ValueTask DisposeAsync() {
		workbench.CommandPanelsChanged -= RefreshTargets;
		return ValueTask.CompletedTask;
	}

	private void ApplyColors(GuiTheme theme) {
		RequestedTheme = theme.ElementTheme;
		root.Background = theme.Background;
		status.Foreground = theme.Muted;
	}

	// --- layout ----------------------------------------------------------

	private void BuildLayout() {
		var up = new Button { Content = "↑", Margin = new Thickness(0, 0, 4, 0) };
		ToolTipService.SetToolTip(up, "Parent folder");
		up.Click += (_, _) => {
			if (SelectedPath is { } path && Directory.GetParent(path) is { } parent) {
				RevealAsync(parent.FullName).Forget("Revealing a folder");
			}
		};

		pathBox.PlaceholderText = "Type a path, press Enter";
		pathBox.KeyDown += (_, e) => {
			if (e.Key == VirtualKey.Enter) {
				RevealAsync(pathBox.Text.Trim().Trim('"')).Forget("Revealing a folder");
				e.Handled = true;
			}
		};

		var top = new Grid { Margin = new Thickness(6, 6, 6, 4) };
		top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		Grid.SetColumn(pathBox, 1);
		top.Children.Add(up);
		top.Children.Add(pathBox);

		tree.SelectionMode = TreeViewSelectionMode.Single;
		tree.CanDragItems = true;
		tree.Expanding += (_, e) => {
			if (e.Node.Content is FolderEntry entry) {
				EnsureLoadedAsync(e.Node, entry).Forget("Loading a folder");
			}
		};

		tree.ItemInvoked += (_, e) => {
			var node = e.InvokedItem as TreeViewNode ?? tree.SelectedNode;

			if (node?.Content is FolderEntry entry) {
				tree.SelectedNode = node;
				OnSelected(entry.Path);
			}
		};

		tree.DragItemsStarting += (_, e) => {
			if (e.Items.FirstOrDefault() is TreeViewNode { Content: FolderEntry entry }) {
				e.Data.SetText(entry.Path);
				e.Data.RequestedOperation = DataPackageOperation.Copy;
			} else {
				e.Cancel = true;
			}
		};

		tree.RightTapped += OnRightTapped;

		// Bottom: where the folder goes, and what to do with it.
		targets.HorizontalAlignment = HorizontalAlignment.Stretch;
		targets.Margin = new Thickness(0, 0, 0, 6);
		ToolTipService.SetToolTip(targets, "The command window that receives the folder");

		var setFolder = new Button { Content = "Set folder" };
		ToolTipService.SetToolTip(setFolder, "Make the selected folder the target Shell's current folder (/cd)");
		setFolder.Click += (_, _) => WithSelection(SendFolder);

		var newWindow = new Button { Content = "New window here", Margin = new Thickness(6, 0, 0, 0) };
		newWindow.Click += (_, _) => WithSelection(path => workbench.OpenCommandWindow(path));

		var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
		buttons.Children.Add(setFolder);
		buttons.Children.Add(newWindow);

		commandBox.FontFamily = new FontFamily(workbench.Fonts.MonoFamily);
		ToolTipService.SetToolTip(commandBox, "Command to run in the selected folder, in the target window");
		commandBox.KeyDown += (_, e) => {
			if (e.Key == VirtualKey.Enter) {
				WithSelection(path => RunHereAsync(path).Forget("Running in a folder"));
				e.Handled = true;
			}
		};

		var run = new Button { Content = "Run", Margin = new Thickness(6, 0, 0, 0) };
		run.Click += (_, _) => WithSelection(path => RunHereAsync(path).Forget("Running in a folder"));

		var runRow = new Grid();
		runRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		runRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		Grid.SetColumn(run, 1);
		runRow.Children.Add(commandBox);
		runRow.Children.Add(run);

		status.Margin = new Thickness(0, 4, 0, 0);
		status.TextWrapping = TextWrapping.Wrap;

		var bottom = new StackPanel { Margin = new Thickness(6, 4, 6, 6) };
		bottom.Children.Add(new TextBlock { Text = "Send to", Margin = new Thickness(0, 0, 0, 2), Opacity = 0.7 });
		bottom.Children.Add(targets);
		bottom.Children.Add(buttons);
		bottom.Children.Add(runRow);
		bottom.Children.Add(status);

		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		Grid.SetRow(tree, 1);
		Grid.SetRow(bottom, 2);
		root.Children.Add(top);
		root.Children.Add(tree);
		root.Children.Add(bottom);
		Content = root;
	}

	private void OnSelected(string path) {
		pathBox.Text = path;
		workbench.State.ExplorerPath = path;
		status.Text = "";
	}

	// --- tree ------------------------------------------------------------

	private async Task RebuildAndRevealAsync(string? path) {
		await BuildRootsAsync();

		if (path is not null) {
			await RevealAsync(path);
		}
	}

	private async Task BuildRootsAsync() {
		var version = ++rootsVersion;
		var bookmarks = workbench.State.Bookmarks.ToList();
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

		// Volume labels and readiness can each take seconds to answer.
		var (existingBookmarks, volumes) = await Task.Run(() => (
			bookmarks.Where(Directory.Exists).ToList(),
			FindVolumes()));

		// A newer rebuild started while this one was waiting.
		if (version != rootsVersion) {
			return;
		}

		tree.RootNodes.Clear();
		volumeRoots.Clear();

		var bookmarkRoot = new TreeViewNode { Content = "Bookmarks", IsExpanded = true };

		foreach (var bookmark in existingBookmarks) {
			bookmarkRoot.Children.Add(CreateNode(bookmark, bookmark));
		}

		if (bookmarkRoot.Children.Count == 0) {
			bookmarkRoot.Children.Add(new TreeViewNode { Content = "(right-click a folder to bookmark it)" });
		}

		tree.RootNodes.Add(bookmarkRoot);
		tree.RootNodes.Add(CreateNode(home, "Home"));

		var computer = new TreeViewNode { Content = OperatingSystem.IsWindows() ? "This PC" : "Computer", IsExpanded = true };

		foreach (var (volumePath, label) in volumes) {
			var node = CreateNode(volumePath, label);
			volumeRoots.Add(node);
			computer.Children.Add(node);
		}

		tree.RootNodes.Add(computer);
	}

	// Drives on Windows; "/" plus real mounted volumes elsewhere (skipping
	// /proc, /sys and the other pseudo filesystems DriveInfo also reports).
	private static List<(string Path, string Label)> FindVolumes() {
		var found = new List<(string Path, string Label)>();

		if (!OperatingSystem.IsWindows()) {
			found.Add(("/", "/"));
		}

		foreach (var drive in DriveInfo.GetDrives()) {
			try {
				if (!drive.IsReady) {
					continue;
				}

				var path = drive.RootDirectory.FullName;

				if (OperatingSystem.IsWindows()) {
					var name = drive.Name.TrimEnd('\\');
					found.Add((path, string.IsNullOrEmpty(drive.VolumeLabel) ? name : $"{name}  {drive.VolumeLabel}"));
					continue;
				}

				var isVolume = path.StartsWith("/Volumes/", StringComparison.Ordinal)
					|| path.StartsWith("/media/", StringComparison.Ordinal)
					|| path.StartsWith("/mnt/", StringComparison.Ordinal)
					|| path.StartsWith("/run/media/", StringComparison.Ordinal);

				if (isVolume && drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network or DriveType.CDRom) {
					found.Add((path, Path.GetFileName(path.TrimEnd('/'))));
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
				// Went away while we asked.
			}
		}

		return found;
	}

	private static TreeViewNode CreateNode(string path, string? label = null) {
		var name = label ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

		return new TreeViewNode {
			Content = new FolderEntry(path, string.IsNullOrEmpty(name) ? path : name),
			HasUnrealizedChildren = true
		};
	}

	private Task EnsureLoadedAsync(TreeViewNode node, FolderEntry entry) {
		return entry.Loading ??= LoadAsync(node, entry);
	}

	private async Task LoadAsync(TreeViewNode node, FolderEntry entry) {
		var showHidden = workbench.Settings.ShowHiddenFolders;

		var (folders, failed) = await Task.Run(() => {
			try {
				// .NET reports dot-folders as Hidden on macOS and Linux too.
				var list = new DirectoryInfo(entry.Path)
					.EnumerateDirectories()
					.Where(folder => showHidden
						|| (folder.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
					.OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
					.Select(folder => folder.FullName)
					.ToList();

				return (list, false);
			}
			catch (Exception exception) when (
				exception is UnauthorizedAccessException or IOException or System.Security.SecurityException) {

				return (new List<string>(), true);
			}
		});

		node.Children.Clear();
		node.HasUnrealizedChildren = false;

		if (failed) {
			node.Children.Add(new TreeViewNode { Content = "(no access)" });
			return;
		}

		foreach (var folder in folders) {
			node.Children.Add(CreateNode(folder));
		}
	}

	// Expands the tree down to path and selects it.
	private async Task RevealAsync(string path) {
		var exists = path.Length > 0 && await Task.Run(() => Directory.Exists(path));

		if (!exists) {
			status.Text = $"Not a folder: {path}";
			return;
		}

		var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
		var comparison = PlatformShell.PathComparison;

		// The volume this path lives on: the longest matching root.
		var node = volumeRoots
			.Where(candidate => candidate.Content is FolderEntry entry && IsUnder(full, entry.Path, comparison))
			.OrderByDescending(candidate => ((FolderEntry)candidate.Content).Path.Length)
			.FirstOrDefault();

		if (node is null) {
			return;
		}

		var current = Path.TrimEndingDirectorySeparator(((FolderEntry)node.Content).Path);
		var remainder = full.Length > current.Length ? full[current.Length..] : "";

		foreach (var segment in remainder.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)) {
			await EnsureLoadedAsync(node, (FolderEntry)node.Content);
			node.IsExpanded = true;
			current = Path.Combine(current, segment);

			var next = node.Children.FirstOrDefault(child =>
				child.Content is FolderEntry entry
				&& string.Equals(Path.TrimEndingDirectorySeparator(entry.Path), current, comparison));

			if (next is null) {
				break;
			}

			node = next;
		}

		tree.SelectedNode = node;
		OnSelected(((FolderEntry)node.Content).Path);
		(tree.ContainerFromNode(node) as FrameworkElement)?.StartBringIntoView();
	}

	private static bool IsUnder(string path, string root, StringComparison comparison) {
		var trimmed = Path.TrimEndingDirectorySeparator(root);

		return string.Equals(path, trimmed, comparison)
			|| path.StartsWith(trimmed.EndsWith(Path.DirectorySeparatorChar) ? trimmed : trimmed + Path.DirectorySeparatorChar, comparison)
			|| trimmed == "/";
	}

	// --- targets and actions ---------------------------------------------

	private void RefreshTargets() {
		var previous = (targets.SelectedItem as TargetChoice)?.Panel;

		var choices = workbench.CommandPanels
			.Select((panel, index) => new TargetChoice($"{index + 1}: {panel.Title}", panel))
			.Append(new TargetChoice("New command window", null))
			.ToList();

		targets.ItemsSource = choices;
		targets.SelectedItem =
			choices.FirstOrDefault(choice => choice.Panel is not null && choice.Panel == (previous ?? workbench.ActiveCommandPanel))
			?? choices[0];
	}

	private CommandPanel? SelectedTarget {
		get {
			var panel = (targets.SelectedItem as TargetChoice)?.Panel;
			return panel is not null && workbench.CommandPanels.Contains(panel) ? panel : null;
		}
	}

	private void WithSelection(Action<string> action) {
		if (SelectedPath is { } path) {
			action(path);
		} else {
			status.Text = "Select a folder first.";
		}
	}

	private void SendFolder(string path) {
		if (SelectedTarget is { } target) {
			workbench.Activate(target);
			target.SetFolderAsync(path).Forget("Setting a folder");
		} else {
			workbench.OpenCommandWindow(path);
		}
	}

	private async Task RunHereAsync(string path) {
		var command = commandBox.Text.Trim();

		if (command.Length == 0) {
			status.Text = "Type a command to run.";
			return;
		}

		if (SelectedTarget is { } target) {
			workbench.Activate(target);
			await target.RunInFolderAsync(path, command);
		} else {
			// A new window starts in the folder; run once its Shell is ready.
			var created = workbench.OpenCommandWindow(path);
			await created.Ready;
			await created.RunAsync(command);
		}
	}

	// --- context menu ------------------------------------------------------

	// Right-click selects the folder under the pointer and offers the actions.
	private void OnRightTapped(object sender, RightTappedRoutedEventArgs e) {
		var source = e.OriginalSource as DependencyObject;

		while (source is not null and not TreeViewItem) {
			source = VisualTreeHelper.GetParent(source);
		}

		if (source is not TreeViewItem item || tree.NodeFromContainer(item) is not { Content: FolderEntry entry } node) {
			return;
		}

		tree.SelectedNode = node;
		OnSelected(entry.Path);

		var path = entry.Path;
		var menu = new MenuFlyout();

		void Add(string text, Action action) {
			var menuItem = new MenuFlyoutItem { Text = text };
			menuItem.Click += (_, _) => action();
			menu.Items.Add(menuItem);
		}

		Add($"Set as folder in {SelectedTarget?.Title ?? "a new command window"}", () => SendFolder(path));
		Add($"Run \"{commandBox.Text.Trim()}\" here", () => RunHereAsync(path).Forget("Running in a folder"));
		Add("Open command window here", () => workbench.OpenCommandWindow(path));
		menu.Items.Add(new MenuFlyoutSeparator());

		var comparison = PlatformShell.PathComparison;
		var bookmarked = workbench.State.Bookmarks.Any(bookmark => string.Equals(bookmark, path, comparison));

		if (bookmarked) {
			Add("Remove bookmark", () => {
				workbench.State.Bookmarks.RemoveAll(bookmark => string.Equals(bookmark, path, comparison));
				RebuildAndRevealAsync(path).Forget("Updating bookmarks");
			});
		} else {
			Add("Bookmark", () => {
				workbench.State.Bookmarks.Add(path);
				RebuildAndRevealAsync(path).Forget("Updating bookmarks");
			});
		}

		Add("Copy path", () => {
			var package = new DataPackage();
			package.SetText(path);
			Clipboard.SetContent(package);
		});

		Add($"Show in {PlatformShell.FileManagerName}", () =>
			Task.Run(() => PlatformShell.RevealFolder(path)).Forget("Opening the file manager"));

		menu.ShowAt(item, e.GetPosition(item));
		e.Handled = true;
	}
}

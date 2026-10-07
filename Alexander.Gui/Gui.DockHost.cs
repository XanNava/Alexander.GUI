using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Windows.Foundation;

namespace Alexander.Gui;

// Draws one window's dock tree and handles the interaction:
//   * click a tab to show it; its ✕ closes it;
//   * drag a tab onto a group: the highlighted edge (or the middle, to
//     join it as a tab) shows where it will land;
//   * drag a tab out of the window to float it in a window of its own;
//   * drag the bars between groups to resize;
//   * right-click a tab for Close / Float / Dock in main window / Split.
internal sealed class DockHost : UserControl {
	private sealed record GroupView(TabGroup Group, Grid View, Border Body, StackPanel Strip);

	private readonly DockManager manager;
	private readonly Grid content = new();
	private readonly Canvas overlay = new() { IsHitTestVisible = false };
	private readonly Border highlight = new() { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(4) };
	private readonly List<GroupView> groupViews = [];
	private readonly Dictionary<string, TextBlock> titles = new();

	public DockHost(DockManager manager, Window window, bool isMain) {
		this.manager = manager;
		Window = window;
		IsMain = isMain;

		overlay.Children.Add(highlight);

		var layer = new Grid();
		layer.Children.Add(content);
		layer.Children.Add(overlay);
		Content = layer;
	}

	public Window Window { get; }

	public bool IsMain { get; }

	public DockNode? Root { get; set; }

	// Rebuilds the visuals from the tree. Panels are reattached, not
	// recreated, so their state (output, scroll, Shell) carries over.
	public void Render() {
		var theme = manager.Theme;
		RequestedTheme = theme.ElementTheme;
		Background = theme.Background;
		highlight.Background = theme.AccentWash;
		highlight.BorderBrush = theme.Accent;
		highlight.BorderThickness = new Thickness(2);

		DetachPanels();
		content.Children.Clear();
		groupViews.Clear();
		titles.Clear();

		if (Root is not null) {
			content.Children.Add(Build(Root));
		}
	}

	// Takes every panel element out of its container, so it can be
	// placed somewhere else.
	public void DetachPanels() {
		foreach (var view in groupViews) {
			view.Body.Child = null;
		}
	}

	public void UpdateTitle(IPanel panel) {
		if (titles.TryGetValue(panel.ContentId, out var title)) {
			title.Text = panel.Title;
		}
	}

	// Shows a tab without rebuilding anything else.
	public void Select(TabGroup group, IPanel panel) {
		manager.Select(group, panel);

		if (groupViews.FirstOrDefault(view => view.Group == group) is not { } groupView) {
			Render();
			return;
		}

		groupView.Body.Child = null;
		groupView.Body.Child = panel.Element;
		StyleTabs(groupView);
	}

	// --- building ---------------------------------------------------------

	private FrameworkElement Build(DockNode node) {
		return node switch {
			SplitNode split => BuildSplit(split),
			TabGroup group => BuildGroup(group),
			_ => new Grid()
		};
	}

	private Grid BuildSplit(SplitNode split) {
		var grid = new Grid();
		var horizontal = split.Orientation == Orientation.Horizontal;

		for (var index = 0; index < split.Children.Count; index++) {
			if (index > 0) {
				AddDefinition(grid, horizontal, new GridLength(5));
				var splitter = CreateSplitter(split, index - 1, grid, horizontal);
				Place(splitter, grid.ColumnDefinitions.Count + grid.RowDefinitions.Count - 1, horizontal);
				grid.Children.Add(splitter);
			}

			AddDefinition(grid, horizontal, new GridLength(Math.Max(0.01, split.Sizes[index]), GridUnitType.Star));
			var child = Build(split.Children[index]);
			Place(child, grid.ColumnDefinitions.Count + grid.RowDefinitions.Count - 1, horizontal);
			grid.Children.Add(child);
		}

		return grid;
	}

	private static void AddDefinition(Grid grid, bool horizontal, GridLength length) {
		if (horizontal) {
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = length });
		} else {
			grid.RowDefinitions.Add(new RowDefinition { Height = length });
		}
	}

	private static void Place(FrameworkElement element, int index, bool horizontal) {
		if (horizontal) {
			Grid.SetColumn(element, index);
		} else {
			Grid.SetRow(element, index);
		}
	}

	// The bar between children first and first + 1. Dragging it moves
	// space between just those two, keeping their combined weight.
	private Border CreateSplitter(SplitNode split, int first, Grid grid, bool horizontal) {
		var bar = new Border { Background = manager.Theme.Border };
		var dragging = false;
		double start = 0, firstPixels = 0, secondPixels = 0, totalWeight = 0;

		double Along(PointerRoutedEventArgs e) {
			var point = e.GetCurrentPoint(grid).Position;
			return horizontal ? point.X : point.Y;
		}

		double Actual(int child) {
			return horizontal
				? grid.ColumnDefinitions[child * 2].ActualWidth
				: grid.RowDefinitions[child * 2].ActualHeight;
		}

		void SetWeight(int child, double weight) {
			var length = new GridLength(Math.Max(0.01, weight), GridUnitType.Star);

			if (horizontal) {
				grid.ColumnDefinitions[child * 2].Width = length;
			} else {
				grid.RowDefinitions[child * 2].Height = length;
			}
		}

		bar.PointerPressed += (_, e) => {
			dragging = bar.CapturePointer(e.Pointer);
			start = Along(e);
			firstPixels = Actual(first);
			secondPixels = Actual(first + 1);
			totalWeight = split.Sizes[first] + split.Sizes[first + 1];
			e.Handled = true;
		};

		bar.PointerMoved += (_, e) => {
			if (!dragging) {
				return;
			}

			var pixels = firstPixels + secondPixels;

			if (pixels <= 0) {
				return;
			}

			var newFirst = Math.Clamp(firstPixels + Along(e) - start, 40, Math.Max(40, pixels - 40));
			split.Sizes[first] = totalWeight * newFirst / pixels;
			split.Sizes[first + 1] = totalWeight - split.Sizes[first];
			SetWeight(first, split.Sizes[first]);
			SetWeight(first + 1, split.Sizes[first + 1]);
		};

		bar.PointerReleased += (_, e) => {
			dragging = false;
			bar.ReleasePointerCapture(e.Pointer);
		};

		bar.PointerCaptureLost += (_, _) => dragging = false;

		return bar;
	}

	private Grid BuildGroup(TabGroup group) {
		var theme = manager.Theme;

		var strip = new StackPanel { Orientation = Orientation.Horizontal };
		var stripScroller = new ScrollViewer {
			Content = strip,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollMode = ScrollMode.Enabled,
			VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
			VerticalScrollMode = ScrollMode.Disabled,
			Background = theme.Surface
		};

		var body = new Border { Background = theme.Background };

		var view = new Grid { Background = theme.Surface };
		view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		view.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		Grid.SetRow(body, 1);
		view.Children.Add(stripScroller);
		view.Children.Add(body);

		var groupView = new GroupView(group, view, body, strip);
		groupViews.Add(groupView);

		group.Selected ??= group.Items.FirstOrDefault();

		foreach (var panel in group.Items) {
			strip.Children.Add(BuildTab(groupView, panel));
		}

		if (group.Selected is { } selected) {
			body.Child = selected.Element;
		} else if (IsMain) {
			body.Child = new TextBlock {
				Text = "File > New command window, or drag a tab here.",
				Foreground = theme.Muted,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			};
		}

		// Focus inside a group makes its panel the active one.
		body.GotFocus += (_, _) => {
			if (group.Selected is { } focused) {
				manager.NoteFocus(group, focused);
			}
		};

		StyleTabs(groupView);
		return view;
	}

	private Border BuildTab(GroupView groupView, IPanel panel) {
		var theme = manager.Theme;

		var title = new TextBlock {
			Text = panel.Title,
			VerticalAlignment = VerticalAlignment.Center,
			MaxWidth = 240,
			TextTrimming = TextTrimming.CharacterEllipsis
		};

		titles[panel.ContentId] = title;

		var close = new Button {
			Content = "✕",
			FontSize = 10,
			Padding = new Thickness(5, 1, 5, 1),
			MinWidth = 0,
			MinHeight = 0,
			Background = theme.Transparent,
			BorderThickness = new Thickness(0),
			VerticalAlignment = VerticalAlignment.Center
		};

		close.Click += (_, _) => manager.Close(panel);
		ToolTipService.SetToolTip(close, "Close");

		var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
		row.Children.Add(title);
		row.Children.Add(close);

		var tab = new Border {
			Child = row,
			Padding = new Thickness(10, 5, 4, 5),
			BorderThickness = new Thickness(0, 0, 0, 2),
			Tag = panel
		};

		AttachTabDrag(tab, groupView, panel);

		tab.RightTapped += (_, e) => {
			ShowTabMenu(tab, groupView.Group, panel, e.GetPosition(tab));
			e.Handled = true;
		};

		return tab;
	}

	private void StyleTabs(GroupView groupView) {
		var theme = manager.Theme;

		foreach (var tab in groupView.Strip.Children.OfType<Border>()) {
			var selected = tab.Tag == groupView.Group.Selected;
			tab.Background = selected ? theme.Background : theme.Surface;
			tab.BorderBrush = selected ? theme.Accent : theme.Transparent;
		}
	}

	private void ShowTabMenu(FrameworkElement tab, TabGroup group, IPanel panel, Point position) {
		var menu = new MenuFlyout();

		void Add(string text, Action action) {
			var item = new MenuFlyoutItem { Text = text };
			item.Click += (_, _) => action();
			menu.Items.Add(item);
		}

		Add("Close", () => manager.Close(panel));
		menu.Items.Add(new MenuFlyoutSeparator());

		if (IsMain) {
			Add("Float in a new window", () => manager.Float(panel));
		} else {
			Add("Dock in main window", () => manager.DockToMain(panel));
		}

		if (group.Items.Count > 1) {
			Add("Split right", () => manager.Dock(panel, group, DockZone.Right));
			Add("Split down", () => manager.Dock(panel, group, DockZone.Bottom));
		}

		menu.ShowAt(tab, position);
	}

	// --- tab dragging -------------------------------------------------------

	private void AttachTabDrag(Border tab, GroupView groupView, IPanel panel) {
		var pressed = false;
		var dragging = false;
		var start = default(Point);

		tab.PointerPressed += (_, e) => {
			var point = e.GetCurrentPoint(this);

			if (!point.Properties.IsLeftButtonPressed) {
				return;
			}

			pressed = tab.CapturePointer(e.Pointer);
			dragging = false;
			start = point.Position;
			e.Handled = true;
		};

		tab.PointerMoved += (_, e) => {
			if (!pressed) {
				return;
			}

			var position = e.GetCurrentPoint(this).Position;

			if (!dragging && (Math.Abs(position.X - start.X) > 8 || Math.Abs(position.Y - start.Y) > 8)) {
				dragging = true;
			}

			if (dragging) {
				ShowDropTarget(position);
			}
		};

		tab.PointerReleased += (_, e) => {
			if (!pressed) {
				return;
			}

			pressed = false;
			tab.ReleasePointerCapture(e.Pointer);
			var position = e.GetCurrentPoint(this).Position;

			if (dragging) {
				dragging = false;
				HideDropTarget();
				DropPanel(panel, position);
			} else {
				Select(groupView.Group, panel);
				panel.Element.Focus(FocusState.Programmatic);
			}

			e.Handled = true;
		};

		tab.PointerCaptureLost += (_, _) => {
			if (dragging) {
				HideDropTarget();
			}

			pressed = dragging = false;
		};
	}

	// Which group (and which part of it) is under position, in this
	// host's coordinates. The outer quarter on each side means "dock
	// beside"; the middle means "join as a tab".
	private (TabGroup Group, DockZone Zone, Rect Area)? HitTest(Point position) {
		foreach (var groupView in groupViews) {
			var bounds = groupView.View
				.TransformToVisual(this)
				.TransformBounds(new Rect(0, 0, groupView.View.ActualWidth, groupView.View.ActualHeight));

			if (!bounds.Contains(position) || bounds.Width <= 0 || bounds.Height <= 0) {
				continue;
			}

			var x = (position.X - bounds.X) / bounds.Width;
			var y = (position.Y - bounds.Y) / bounds.Height;

			var zone = x < 0.25 ? DockZone.Left
				: x > 0.75 ? DockZone.Right
				: y < 0.25 ? DockZone.Top
				: y > 0.75 ? DockZone.Bottom
				: DockZone.Center;

			var area = zone switch {
				DockZone.Left => new Rect(bounds.X, bounds.Y, bounds.Width / 2, bounds.Height),
				DockZone.Right => new Rect(bounds.X + bounds.Width / 2, bounds.Y, bounds.Width / 2, bounds.Height),
				DockZone.Top => new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height / 2),
				DockZone.Bottom => new Rect(bounds.X, bounds.Y + bounds.Height / 2, bounds.Width, bounds.Height / 2),
				_ => bounds
			};

			return (groupView.Group, zone, area);
		}

		return null;
	}

	private void ShowDropTarget(Point position) {
		if (HitTest(position) is not { } hit) {
			HideDropTarget();
			return;
		}

		Canvas.SetLeft(highlight, hit.Area.X);
		Canvas.SetTop(highlight, hit.Area.Y);
		highlight.Width = hit.Area.Width;
		highlight.Height = hit.Area.Height;
		highlight.Visibility = Visibility.Visible;
	}

	private void HideDropTarget() {
		highlight.Visibility = Visibility.Collapsed;
	}

	private void DropPanel(IPanel panel, Point position) {
		if (HitTest(position) is { } hit) {
			manager.Dock(panel, hit.Group, hit.Zone);
			return;
		}

		// Released outside this window: float it.
		if (!new Rect(0, 0, ActualWidth, ActualHeight).Contains(position)) {
			manager.Float(panel);
		}
	}
}

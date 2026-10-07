using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Graphics;

namespace Alexander.Gui;

// The docking model. Each window (main, plus any floating ones) has a
// tree of:
//   SplitNode - children side by side (Horizontal) or stacked (Vertical),
//               sized by star weights, with draggable splitters between;
//   TabGroup  - panels shown as tabs, one visible at a time.
//
// Uno has no docking control, so this is a small one: DockManager owns
// the trees and every operation on them; DockHost (one per window)
// draws a tree and turns tab drags into Dock/Float calls.

internal enum DockZone {
	Center,
	Left,
	Right,
	Top,
	Bottom
}

internal abstract class DockNode {
	public SplitNode? Parent { get; set; }
}

internal sealed class SplitNode(Orientation orientation) : DockNode {
	public Orientation Orientation { get; } = orientation;
	public List<DockNode> Children { get; } = [];
	public List<double> Sizes { get; } = [];

	public void Insert(int index, DockNode child, double size) {
		Children.Insert(index, child);
		Sizes.Insert(index, size);
		child.Parent = this;
	}

	public void RemoveAt(int index) {
		Children[index].Parent = null;
		Children.RemoveAt(index);
		Sizes.RemoveAt(index);
	}

	public void Replace(DockNode old, DockNode replacement) {
		var index = Children.IndexOf(old);
		Children[index] = replacement;
		replacement.Parent = this;
		old.Parent = null;
	}
}

internal sealed class TabGroup : DockNode {
	public List<IPanel> Items { get; } = [];
	public IPanel? Selected { get; set; }
}

internal sealed class DockManager {
	private readonly IWorkbench workbench;
	private readonly List<DockHost> floating = [];
	private TabGroup? lastDocumentGroup;
	private bool closingAll;

	public DockManager(IWorkbench workbench, Window mainWindow) {
		this.workbench = workbench;
		Main = new DockHost(this, mainWindow, isMain: true) { Root = new TabGroup() };
	}

	public DockHost Main { get; }

	public GuiTheme Theme => workbench.Theme;

	// A panel left the layout for good (its tab was closed).
	public event Action<IPanel>? PanelClosed;

	// A panel was focused - for "active command window" tracking.
	public event Action<IPanel>? PanelFocused;

	public IEnumerable<DockHost> Hosts => floating.Prepend(Main);

	public IEnumerable<IPanel> Panels => Hosts.SelectMany(host => Groups(host.Root)).SelectMany(group => group.Items);

	public static IEnumerable<TabGroup> Groups(DockNode? node) {
		return node switch {
			TabGroup group => [group],
			SplitNode split => split.Children.SelectMany(Groups),
			_ => []
		};
	}

	// --- adding and removing ------------------------------------------

	public void BuildDefault(IPanel explorer) {
		var tools = new TabGroup();
		tools.Items.Add(explorer);
		tools.Selected = explorer;

		var documents = new TabGroup();
		var root = new SplitNode(Orientation.Horizontal);
		root.Insert(0, tools, 0.25);
		root.Insert(1, documents, 0.75);

		Main.Root = root;
		lastDocumentGroup = documents;
		Main.Render();
	}

	public void AddDocument(IPanel panel) {
		var group = DocumentGroup();
		group.Items.Add(panel);
		group.Selected = panel;
		Main.Render();
	}

	// A tool panel in a new group along one edge of the main window.
	public void AddTool(IPanel panel, DockZone side) {
		var group = new TabGroup();
		group.Items.Add(panel);
		group.Selected = panel;
		SplitAt(Main, Main.Root!, group, side, addedFraction: 0.25);
		Main.Render();
	}

	public void Close(IPanel panel) {
		if (Remove(panel) is { } host) {
			Refresh(host);
		}

		PanelClosed?.Invoke(panel);
	}

	public void Activate(IPanel panel) {
		if (Find(panel) is not { } found) {
			return;
		}

		var (host, group) = found;

		host.Select(group, panel);

		if (!host.IsMain) {
			host.Window.Activate();
		}
	}

	public void Select(TabGroup group, IPanel panel) {
		group.Selected = panel;
		NoteFocus(group, panel);
	}

	public void NoteFocus(TabGroup group, IPanel panel) {
		if (!panel.IsTool && Groups(Main.Root).Contains(group)) {
			lastDocumentGroup = group;
		}

		PanelFocused?.Invoke(panel);
	}

	// After a theme change: every window redraws its frame.
	public void RenderAll() {
		foreach (var host in Hosts) {
			host.Render();
		}
	}

	public void UpdateTitle(IPanel panel) {
		foreach (var host in Hosts) {
			host.UpdateTitle(panel);
		}
	}

	// --- docking --------------------------------------------------------

	// Moves panel next to (or into) target. A no-op for "onto itself".
	public void Dock(IPanel panel, TabGroup target, DockZone zone) {
		if (Find(panel) is not { } found) {
			return;
		}

		var (sourceHost, sourceGroup) = found;

		if (sourceGroup == target && (zone == DockZone.Center || target.Items.Count == 1)) {
			return;
		}

		var targetHost = HostOf(target);

		if (targetHost is null) {
			return;
		}

		Remove(panel);

		if (zone == DockZone.Center) {
			target.Items.Add(panel);
			target.Selected = panel;
		} else {
			var group = new TabGroup();
			group.Items.Add(panel);
			group.Selected = panel;
			SplitAt(targetHost, target, group, zone, addedFraction: 0.5);
		}

		Refresh(sourceHost);

		if (targetHost != sourceHost) {
			Refresh(targetHost);
		}
	}

	// Moves panel into a new window of its own.
	public void Float(IPanel panel) {
		if (Find(panel) is not { } found) {
			return;
		}

		var (host, _) = found;

		// Already alone in a floating window.
		if (!host.IsMain && Groups(host.Root).Sum(group => group.Items.Count) == 1) {
			return;
		}

		var width = Math.Max(480, panel.Element.ActualWidth);
		var height = Math.Max(320, panel.Element.ActualHeight);

		Remove(panel);
		Refresh(host);

		var group = new TabGroup();
		group.Items.Add(panel);
		group.Selected = panel;
		OpenFloating(group, width, height);
	}

	// Moves a panel from a floating window back into the main one.
	public void DockToMain(IPanel panel) {
		if (Find(panel) is not { } found || found.Host.IsMain) {
			return;
		}

		var host = found.Host;

		Remove(panel);
		Refresh(host);
		PlaceInMain(panel);
		Main.Render();
	}

	// --- persistence ----------------------------------------------------

	public DockLayoutState Capture() {
		return new DockLayoutState {
			Root = CaptureNode(Main.Root),
			Floating = floating
				.Where(host => host.Root is not null)
				.Select(host => new FloatingState {
					Root = CaptureNode(host.Root),
					Width = host.ActualWidth,
					Height = host.ActualHeight
				})
				.ToList()
		};
	}

	// resolve maps a ContentId to its (already created) panel, or null to
	// drop it. Returns the panels placed.
	public HashSet<IPanel> Restore(DockLayoutState state, Func<string, IPanel?> resolve) {
		var placed = new HashSet<IPanel>();

		Main.Root = Prune(RestoreNode(state.Root, resolve, placed)) ?? new TabGroup();
		Main.Render();

		foreach (var window in state.Floating) {
			if (Prune(RestoreNode(window.Root, resolve, placed)) is { } root) {
				OpenFloating(root, window.Width, window.Height);
			}
		}

		return placed;
	}

	// Closes floating windows without docking their panels back (shutdown).
	public void CloseFloatingWindows() {
		closingAll = true;

		foreach (var host in floating.ToList()) {
			host.Window.Close();
		}
	}

	// --- internals ------------------------------------------------------

	private (DockHost Host, TabGroup Group)? Find(IPanel panel) {
		foreach (var host in Hosts) {
			foreach (var group in Groups(host.Root)) {
				if (group.Items.Contains(panel)) {
					return (host, group);
				}
			}
		}

		return null;
	}

	private DockHost? HostOf(TabGroup group) {
		return Hosts.FirstOrDefault(host => Groups(host.Root).Contains(group));
	}

	// Where new documents go: the document group used last, else any group
	// holding documents, else an empty group, else a new one on the right.
	private TabGroup DocumentGroup() {
		var groups = Groups(Main.Root).ToList();

		if (lastDocumentGroup is { } last && groups.Contains(last)) {
			return last;
		}

		var group = groups.FirstOrDefault(candidate => candidate.Items.Any(panel => !panel.IsTool))
			?? groups.FirstOrDefault(candidate => candidate.Items.Count == 0);

		if (group is null) {
			group = new TabGroup();
			SplitAt(Main, Main.Root!, group, DockZone.Right, addedFraction: 0.75);
		}

		lastDocumentGroup = group;
		return group;
	}

	private void PlaceInMain(IPanel panel) {
		if (panel.IsTool) {
			var group = new TabGroup();
			group.Items.Add(panel);
			group.Selected = panel;
			SplitAt(Main, Main.Root!, group, DockZone.Left, addedFraction: 0.25);
		} else {
			var group = DocumentGroup();
			group.Items.Add(panel);
			group.Selected = panel;
		}
	}

	// Takes a panel out of whatever group holds it, collapsing groups and
	// splits that end up empty. Returns the host it came from.
	private DockHost? Remove(IPanel panel) {
		if (Find(panel) is not { } found) {
			return null;
		}

		var (host, group) = found;

		// Its element must leave the old container before it can go anywhere else.
		host.DetachPanels();

		group.Items.Remove(panel);

		if (group.Selected == panel) {
			group.Selected = group.Items.LastOrDefault();
		}

		if (group.Items.Count == 0) {
			RemoveNode(host, group);
		}

		return host;
	}

	private static void RemoveNode(DockHost host, DockNode node) {
		if (node.Parent is not { } parent) {
			// The root itself. The main window always keeps one (empty)
			// group, so there's somewhere to put documents.
			host.Root = host.IsMain ? new TabGroup() : null;
			return;
		}

		parent.RemoveAt(parent.Children.IndexOf(node));

		if (parent.Children.Count == 1) {
			var only = parent.Children[0];
			parent.RemoveAt(0);
			ReplaceNode(host, parent, only);
		}
	}

	private static void ReplaceNode(DockHost host, DockNode old, DockNode replacement) {
		if (old.Parent is { } parent) {
			parent.Replace(old, replacement);
		} else {
			replacement.Parent = null;
			host.Root = replacement;
		}
	}

	// Puts added beside target on the zone's side, taking addedFraction of
	// target's space - into the existing split if it runs the same way.
	private static void SplitAt(DockHost host, DockNode target, DockNode added, DockZone zone, double addedFraction) {
		var orientation = zone is DockZone.Left or DockZone.Right ? Orientation.Horizontal : Orientation.Vertical;
		var before = zone is DockZone.Left or DockZone.Top;

		if (target.Parent is { } parent && parent.Orientation == orientation) {
			var index = parent.Children.IndexOf(target);
			var size = parent.Sizes[index];
			parent.Sizes[index] = size * (1 - addedFraction);
			parent.Insert(before ? index : index + 1, added, size * addedFraction);
			return;
		}

		var split = new SplitNode(orientation);
		ReplaceNode(host, target, split);
		split.Insert(0, before ? added : target, before ? addedFraction : 1 - addedFraction);
		split.Insert(1, before ? target : added, before ? 1 - addedFraction : addedFraction);
	}

	private void Refresh(DockHost host) {
		if (!host.IsMain && host.Root is null) {
			// Its last panel left: the window goes.
			floating.Remove(host);
			host.Window.Close();
		} else {
			host.Render();
		}
	}

	private DockHost OpenFloating(DockNode root, double width, double height) {
		var window = new Window { Title = "Alexander" };
		var host = new DockHost(this, window, isMain: false) { Root = root };

		window.Content = host;
		floating.Add(host);
		host.Render();

		// Closing a floating window docks its panels back rather than
		// closing them - a command window's Shell shouldn't vanish because
		// its window did.
		window.Closed += (_, _) => {
			floating.Remove(host);

			if (closingAll || host.Root is null) {
				return;
			}

			var panels = Groups(host.Root).SelectMany(group => group.Items).ToList();
			host.DetachPanels();
			host.Root = null;

			foreach (var panel in panels) {
				PlaceInMain(panel);
			}

			Main.Render();
		};

		try {
			window.AppWindow.Resize(new SizeInt32 {
				Width = (int)width,
				Height = (int)height
			});
		}
		catch {
			// Not supported on every desktop host; the default size is fine.
		}

		window.Activate();
		return host;
	}

	private static DockNodeState? CaptureNode(DockNode? node) {
		return node switch {
			SplitNode split => new DockNodeState {
				Kind = "split",
				Horizontal = split.Orientation == Orientation.Horizontal,
				Sizes = [.. split.Sizes],
				Children = split.Children.Select(CaptureNode).OfType<DockNodeState>().ToList()
			},
			TabGroup group => new DockNodeState {
				Kind = "tabs",
				Panels = group.Items.Select(panel => panel.ContentId).ToList(),
				Selected = group.Selected is { } selected ? group.Items.IndexOf(selected) : 0
			},
			_ => null
		};
	}

	private static DockNode? RestoreNode(DockNodeState? state, Func<string, IPanel?> resolve, HashSet<IPanel> placed) {
		if (state is null) {
			return null;
		}

		if (state.Kind == "split") {
			var split = new SplitNode(state.Horizontal ? Orientation.Horizontal : Orientation.Vertical);

			for (var index = 0; index < state.Children.Count; index++) {
				if (RestoreNode(state.Children[index], resolve, placed) is { } child) {
					split.Insert(split.Children.Count, child, index < state.Sizes.Count ? state.Sizes[index] : 1);
				}
			}

			return split;
		}

		var group = new TabGroup();

		foreach (var id in state.Panels) {
			if (resolve(id) is { } panel && placed.Add(panel)) {
				group.Items.Add(panel);
			}
		}

		group.Selected = group.Items.ElementAtOrDefault(state.Selected) ?? group.Items.FirstOrDefault();
		return group;
	}

	// Drops empty groups and single-child splits left by panels that
	// didn't come back.
	private static DockNode? Prune(DockNode? node) {
		switch (node) {
			case TabGroup group:
				return group.Items.Count > 0 ? group : null;

			case SplitNode split:
				var kept = split.Children
					.Select((child, index) => (Child: Prune(child), Size: split.Sizes[index]))
					.Where(entry => entry.Child is not null)
					.ToList();

				if (kept.Count == 0) {
					return null;
				}

				if (kept.Count == 1) {
					kept[0].Child!.Parent = null;
					return kept[0].Child;
				}

				var rebuilt = new SplitNode(split.Orientation);

				foreach (var (child, size) in kept) {
					rebuilt.Insert(rebuilt.Children.Count, child!, size);
				}

				return rebuilt;

			default:
				return null;
		}
	}
}

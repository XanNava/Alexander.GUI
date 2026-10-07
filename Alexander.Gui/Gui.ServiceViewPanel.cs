using Alexander.Hosting;

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using SkiaSharp;

using Uno.WinUI.Graphics2DSK;

using Windows.System;

using G = Alexander.Graphics;

namespace Alexander.Gui;

// A dockable window showing one service's custom view. The view itself
// lives in the owning command window's Shell, next to Core; it renders
// there into a display list, which arrives here as a frame and is
// replayed with Skia. Input goes the other way. Closing the owning
// command window (or its Shell exiting) closes its views.
internal sealed class ServiceViewPanel : UserControl, IPanel {
	private static long nextInstance;

	private readonly IWorkbench workbench;
	private readonly ShellSession session;
	private readonly RemoteViewCanvas canvas;
	private readonly Grid surface = new();
	private string title;
	private bool opened;
	private bool closed;
	private int resizeVersion;

	// Pointer moves are coalesced: at most one in flight, newest wins.
	private Windows.Foundation.Point? pendingMove;
	private bool moveInFlight;
	private G.KeyModifiers heldKeys;

	public ServiceViewPanel(IWorkbench workbench, CommandPanel owner, string viewId, string? title, string? contentId) {
		this.workbench = workbench;
		this.title = title ?? viewId;

		Owner = owner;
		ViewId = viewId;
		ContentId = contentId ?? $"{PanelKinds.View}:{Guid.NewGuid():N}";
		Instance = Interlocked.Increment(ref nextInstance);
		session = owner.Session;

		canvas = new RemoteViewCanvas(workbench);

		// The grid (with a background) takes the input, so the whole area
		// is hit-testable however the canvas element handles it.
		surface.Background = workbench.Theme.Background;
		surface.Children.Add(canvas);
		Content = surface;
		IsTabStop = true;

		session.Frame += OnFrame;
		session.ViewClosed += OnViewClosed;

		surface.SizeChanged += (_, _) => ResizeSoonAsync().Forget("Resizing a view");
		surface.PointerMoved += (_, e) => {
			pendingMove = e.GetCurrentPoint(surface).Position;
			SendMovesAsync().Forget("Sending pointer input");
		};

		surface.PointerPressed += (_, e) => {
			Focus(FocusState.Pointer);
			surface.CapturePointer(e.Pointer);
			Send(ViewInputKinds.Down, e);
			e.Handled = true;
		};

		surface.PointerReleased += (_, e) => {
			surface.ReleasePointerCapture(e.Pointer);
			Send(ViewInputKinds.Up, e);
			e.Handled = true;
		};

		surface.PointerExited += (_, e) => {
			pendingMove = null;
			Send(ViewInputKinds.Leave, e);
		};

		surface.PointerWheelChanged += (_, e) => {
			var point = e.GetCurrentPoint(surface);
			session
				.ViewInputAsync(Instance, ViewInputKinds.Wheel, point.Position.X, point.Position.Y, null,
					point.Properties.MouseWheelDelta / 120.0, null, Modifiers(e.KeyModifiers))
				.Forget("Sending wheel input");

			e.Handled = true;
		};

		KeyDown += (_, e) => {
			heldKeys |= ModifierFor(e.Key);
			session
				.ViewInputAsync(Instance, ViewInputKinds.Key, 0, 0, null, 0, e.Key.ToString(), heldKeys)
				.Forget("Sending key input");
		};

		KeyUp += (_, e) => heldKeys &= ~ModifierFor(e.Key);
		GotFocus += (_, _) => workbench.PanelFocused(this);
		Loaded += (_, _) => OpenAsync().Forget("Opening a service view");
	}

	public CommandPanel Owner { get; }

	public string ViewId { get; }

	public long Instance { get; }

	public string ContentId { get; }

	public string Title => title;

	public FrameworkElement Element => this;

	public bool IsTool => false;

	public PanelState Capture() {
		return new PanelState {
			ContentId = ContentId,
			Kind = PanelKinds.View,
			Title = Title,
			ViewId = ViewId,
			OwnerId = Owner.ContentId
		};
	}

	// The Shell re-renders every view when the owner sends it the new
	// theme; only the background shows before that frame arrives.
	public void ApplySettings(GuiSettings settings, GuiTheme theme) {
		surface.Background = theme.Background;
		canvas.Invalidate();
	}

	public async ValueTask DisposeAsync() {
		closed = true;
		session.Frame -= OnFrame;
		session.ViewClosed -= OnViewClosed;

		if (opened) {
			try {
				await session.CloseViewAsync(Instance);
			}
			catch {
				// The Shell may already be gone.
			}
		}
	}

	// Waits for the owning Shell to be ready (restored views are created
	// before it is), then asks it for the view at the current size.
	private async Task OpenAsync() {
		if (opened || closed) {
			return;
		}

		opened = true;
		await Owner.Ready;

		if (closed || !session.IsReady) {
			workbench.ClosePanel(this);
			return;
		}

		await session.OpenViewAsync(Instance, ViewId, surface.ActualWidth, surface.ActualHeight);
	}

	// Resizes are batched: dragging a splitter shouldn't send dozens.
	private async Task ResizeSoonAsync() {
		var version = ++resizeVersion;
		await Task.Delay(120);

		if (version == resizeVersion && opened && !closed) {
			await session.ResizeViewAsync(Instance, surface.ActualWidth, surface.ActualHeight);
		}
	}

	private void OnFrame(long instance, string? frameTitle, IReadOnlyList<G.DrawOp> ops) {
		if (instance != Instance || closed) {
			return;
		}

		canvas.Show(ops);

		if (!string.IsNullOrEmpty(frameTitle) && frameTitle != title) {
			title = frameTitle;
			workbench.PanelTitleChanged(this);
		}
	}

	private void OnViewClosed(long instance, string? reason) {
		if (instance != Instance || closed) {
			return;
		}

		Owner.Report($"Service view '{title}' closed: {reason}", isError: true);
		workbench.ClosePanel(this);
	}

	private async Task SendMovesAsync() {
		if (moveInFlight) {
			return;
		}

		moveInFlight = true;

		try {
			while (pendingMove is { } position) {
				pendingMove = null;
				await session.ViewInputAsync(Instance, ViewInputKinds.Move, position.X, position.Y, null, 0, null, heldKeys);
			}
		}
		finally {
			moveInFlight = false;
		}
	}

	private void Send(string kind, PointerRoutedEventArgs e) {
		var point = e.GetCurrentPoint(surface);

		var button = point.Properties.PointerUpdateKind switch {
			PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => nameof(G.PointerButton.Left),
			PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => nameof(G.PointerButton.Right),
			PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => nameof(G.PointerButton.Middle),
			_ => null
		};

		session
			.ViewInputAsync(Instance, kind, point.Position.X, point.Position.Y, button, 0, null, Modifiers(e.KeyModifiers))
			.Forget("Sending pointer input");
	}

	private static G.KeyModifiers Modifiers(VirtualKeyModifiers keys) {
		var modifiers = G.KeyModifiers.None;

		if (keys.HasFlag(VirtualKeyModifiers.Shift)) {
			modifiers |= G.KeyModifiers.Shift;
		}

		if (keys.HasFlag(VirtualKeyModifiers.Control)) {
			modifiers |= G.KeyModifiers.Control;
		}

		if (keys.HasFlag(VirtualKeyModifiers.Menu)) {
			modifiers |= G.KeyModifiers.Alt;
		}

		return modifiers;
	}

	private static G.KeyModifiers ModifierFor(VirtualKey key) {
		return key switch {
			VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => G.KeyModifiers.Shift,
			VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => G.KeyModifiers.Control,
			VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu => G.KeyModifiers.Alt,
			_ => G.KeyModifiers.None
		};
	}
}

// Replays the latest frame with Skia, drawing straight onto the canvas
// Uno renders the window with.
internal sealed class RemoteViewCanvas(IWorkbench workbench) : SKCanvasElement {
	private IReadOnlyList<G.DrawOp> ops = [];

	public void Show(IReadOnlyList<G.DrawOp> frame) {
		ops = frame;
		Invalidate();
	}

	protected override void RenderOverride(SKCanvas canvas, Windows.Foundation.Size area) {
		var theme = workbench.Theme.View;
		var graphics = new SkiaGraphics(canvas, new G.Size(area.Width, area.Height), theme, workbench.Fonts);

		try {
			graphics.Clear(theme.Background);

			if (ops.Count == 0) {
				graphics.DrawText("Waiting for the Shell…", new G.Point(12, 12), theme.MutedText);
			} else {
				G.DisplayList.Replay(ops, graphics);
			}
		}
		finally {
			graphics.Finish();
		}
	}
}

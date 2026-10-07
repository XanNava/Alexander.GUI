using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

using G = Alexander.Graphics;

namespace Alexander.Gui;

// Brushes built from the shared graphics palette, so the GUI's own panels
// and every service view use the same colors.
internal sealed class GuiTheme {
	private GuiTheme(G.ViewTheme view, bool isDark) {
		View = view;
		IsDark = isDark;
		Background = Brush(view.Background);
		Surface = Brush(view.Surface);
		Border = Brush(view.Border);
		Text = Brush(view.Text);
		Muted = Brush(view.MutedText);
		Accent = Brush(view.Accent);
		Danger = Brush(view.Danger);
		Success = Brush(view.Success);
		Transparent = new SolidColorBrush(Colors.Transparent);
		AccentWash = Brush(view.Accent.WithAlpha(70));
	}

	public G.ViewTheme View { get; }
	public bool IsDark { get; }

	// For the built-in controls (TextBox, MenuBar, ...).
	public ElementTheme ElementTheme => IsDark ? ElementTheme.Dark : ElementTheme.Light;

	public Brush Background { get; }
	public Brush Surface { get; }
	public Brush Border { get; }
	public Brush Text { get; }
	public Brush Muted { get; }
	public Brush Accent { get; }
	public Brush Danger { get; }
	public Brush Success { get; }
	public Brush Transparent { get; }

	// Drop-target highlight while dragging a tab.
	public Brush AccentWash { get; }

	public static GuiTheme From(GuiSettings settings) {
		return settings.Theme == "light"
			? new GuiTheme(G.ViewTheme.Light, isDark: false)
			: new GuiTheme(G.ViewTheme.Dark, isDark: true);
	}

	public static SolidColorBrush Brush(G.Color color) {
		return new SolidColorBrush(ColorHelper.FromArgb(color.A, color.R, color.G, color.B));
	}
}

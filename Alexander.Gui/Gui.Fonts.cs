using SkiaSharp;

using G = Alexander.Graphics;

namespace Alexander.Gui;

// Picks fonts that actually exist on this machine. Font names differ per
// OS (Segoe UI / SF / Ubuntu, Consolas / Menlo / DejaVu Sans Mono), so
// each setting is a preference list and the first installed name wins.
internal static class FontResolver {
	public static readonly string[] UiFallbacks = [
		"Segoe UI", "SF Pro Text", "Helvetica Neue", "Ubuntu", "Cantarell",
		"Noto Sans", "DejaVu Sans", "Liberation Sans", "Arial"
	];

	public static readonly string[] MonoFallbacks = [
		"Cascadia Mono", "Consolas", "SF Mono", "Menlo", "JetBrains Mono",
		"DejaVu Sans Mono", "Liberation Mono", "Noto Sans Mono", "Courier New"
	];

	private static readonly Lazy<HashSet<string>> Installed = new(() =>
		new HashSet<string>(SKFontManager.Default.FontFamilies, StringComparer.OrdinalIgnoreCase));

	public static string ResolveUi() {
		return Resolve("", UiFallbacks);
	}

	public static string ResolveMono(string preferences) {
		return Resolve(preferences, MonoFallbacks);
	}

	private static string Resolve(string preferences, string[] fallbacks) {
		var candidates = preferences
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Concat(fallbacks);

		return candidates.FirstOrDefault(Installed.Value.Contains)
			?? SKTypeface.Default.FamilyName;
	}
}

// The Skia fonts service views are drawn and measured with. Built once
// per font setting and shared by every view.
internal sealed class FontSet {
	private readonly SKTypeface uiRegular;
	private readonly SKTypeface uiBold;
	private readonly SKTypeface monoRegular;
	private readonly SKTypeface monoBold;
	private readonly Dictionary<(bool Mono, bool Bold, float Size), SKFont> cache = new();

	public FontSet(string monoPreferences) {
		UiFamily = FontResolver.ResolveUi();
		MonoFamily = FontResolver.ResolveMono(monoPreferences);

		uiRegular = Typeface(UiFamily, SKFontStyleWeight.Normal);
		uiBold = Typeface(UiFamily, SKFontStyleWeight.SemiBold);
		monoRegular = Typeface(MonoFamily, SKFontStyleWeight.Normal);
		monoBold = Typeface(MonoFamily, SKFontStyleWeight.SemiBold);
	}

	public string UiFamily { get; }

	// Also used for the command windows' text, so everything matches.
	public string MonoFamily { get; }

	public SKFont Get(G.TextStyle? style) {
		style ??= G.TextStyle.Body;
		var key = (style.Monospace, style.Bold, (float)style.FontSize);

		lock (cache) {
			if (!cache.TryGetValue(key, out var font)) {
				var typeface = style.Monospace
					? style.Bold ? monoBold : monoRegular
					: style.Bold ? uiBold : uiRegular;

				font = new SKFont(typeface, key.Item3) { Subpixel = true };
				cache[key] = font;
			}

			return font;
		}
	}

	// Measures the GUI's real fonts and packages the result for the Shell,
	// so views laying text out over there (RecordingGraphics) get widths
	// matching what's drawn here.
	public G.TextMetrics CreateMetrics() {
		const float size = 100;

		double[] Widths(SKTypeface typeface) {
			using var font = new SKFont(typeface, size) { Subpixel = true };
			var widths = new double[G.TextMetrics.Count];

			for (var index = 0; index < widths.Length; index++) {
				widths[index] = font.MeasureText(((char)(G.TextMetrics.FirstChar + index)).ToString()) / size;
			}

			return widths;
		}

		double LineHeight(SKTypeface typeface) {
			using var font = new SKFont(typeface, size);
			var metrics = font.Metrics;
			return (metrics.Descent - metrics.Ascent + metrics.Leading) / size;
		}

		return new G.TextMetrics {
			UiRegular = Widths(uiRegular),
			UiBold = Widths(uiBold),
			MonoRegular = Widths(monoRegular),
			MonoBold = Widths(monoBold),
			UiLineHeight = LineHeight(uiRegular),
			MonoLineHeight = LineHeight(monoRegular)
		};
	}

	private static SKTypeface Typeface(string family, SKFontStyleWeight weight) {
		return SKTypeface.FromFamilyName(family, weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
			?? SKTypeface.Default;
	}
}

using SkiaSharp;

using G = Alexander.Graphics;

namespace Alexander.Gui;

// The shared graphics library's IGraphics, drawn with Skia - the same
// engine Uno renders the whole window with, so it looks identical on
// Windows, macOS and Linux. Created per frame over the view's canvas.
internal sealed class SkiaGraphics(SKCanvas canvas, G.Size size, G.ViewTheme theme, FontSet fonts) : G.IGraphics {
	private int clips;

	public G.Size Size => size;

	public G.ViewTheme Theme => theme;

	public void Clear(G.Color color) {
		using var paint = Fill(color);
		canvas.DrawRect(new SKRect(0, 0, (float)size.Width, (float)size.Height), paint);
	}

	public void FillRect(G.Rect rect, G.Color color, double cornerRadius = 0) {
		using var paint = Fill(color);
		DrawRectangle(rect, cornerRadius, paint);
	}

	public void DrawRect(G.Rect rect, G.Color color, double thickness = 1, double cornerRadius = 0) {
		using var paint = Stroke(color, thickness);
		DrawRectangle(rect, cornerRadius, paint);
	}

	public void DrawLine(G.Point from, G.Point to, G.Color color, double thickness = 1) {
		using var paint = Stroke(color, thickness);
		canvas.DrawLine((float)from.X, (float)from.Y, (float)to.X, (float)to.Y, paint);
	}

	public void FillEllipse(G.Rect bounds, G.Color color) {
		using var paint = Fill(color);
		canvas.DrawOval(ToSk(bounds), paint);
	}

	public void DrawEllipse(G.Rect bounds, G.Color color, double thickness = 1) {
		using var paint = Stroke(color, thickness);
		canvas.DrawOval(ToSk(bounds), paint);
	}

	// origin is the top-left of the text box; Skia draws from the baseline.
	public void DrawText(string text, G.Point origin, G.Color color, G.TextStyle? style = null) {
		var font = fonts.Get(style);
		using var paint = Fill(color);
		canvas.DrawText(text, (float)origin.X, (float)origin.Y - font.Metrics.Ascent, SKTextAlign.Left, font, paint);
	}

	public G.Size MeasureText(string text, G.TextStyle? style = null) {
		var font = fonts.Get(style);
		var metrics = font.Metrics;
		return new G.Size(font.MeasureText(text), metrics.Descent - metrics.Ascent + metrics.Leading);
	}

	public void PushClip(G.Rect rect) {
		canvas.Save();
		canvas.ClipRect(ToSk(rect));
		clips++;
	}

	public void PopClip() {
		if (clips > 0) {
			canvas.Restore();
			clips--;
		}
	}

	// Pops any clips a view forgot, so one bad view can't corrupt the frame.
	public void Finish() {
		while (clips > 0) {
			PopClip();
		}
	}

	private void DrawRectangle(G.Rect rect, double cornerRadius, SKPaint paint) {
		if (cornerRadius > 0) {
			canvas.DrawRoundRect(ToSk(rect), (float)cornerRadius, (float)cornerRadius, paint);
		} else {
			canvas.DrawRect(ToSk(rect), paint);
		}
	}

	private static SKPaint Fill(G.Color color) {
		return new SKPaint { Color = ToSk(color), IsAntialias = true, Style = SKPaintStyle.Fill };
	}

	private static SKPaint Stroke(G.Color color, double thickness) {
		return new SKPaint { Color = ToSk(color), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)thickness };
	}

	private static SKColor ToSk(G.Color color) {
		return new SKColor(color.R, color.G, color.B, color.A);
	}

	private static SKRect ToSk(G.Rect rect) {
		return SKRect.Create((float)rect.X, (float)rect.Y, (float)Math.Max(0, rect.Width), (float)Math.Max(0, rect.Height));
	}
}

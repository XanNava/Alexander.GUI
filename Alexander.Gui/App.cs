using System.Reflection;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Alexander.Gui;

// Code-only app (no App.xaml): default control styles come from
// XamlControlsResources, everything else is built in code.
public sealed class App : Application {
	private MainWindow? main;

	protected override void OnLaunched(LaunchActivatedEventArgs args) {
		Resources ??= new ResourceDictionary();
		Resources.MergedDictionaries.Add(new XamlControlsResources());
		UiThread.Initialize();

		// Last line of defence: report and keep running rather than crash.
		UnhandledException += (_, e) => {
			Diagnostics.Report("Unexpected error", e.Exception);
			e.Handled = true;
		};

		TaskScheduler.UnobservedTaskException += (_, e) => {
			Diagnostics.Report("Background task", e.Exception, quiet: true);
			e.SetObserved();
		};

		main = new MainWindow(GuiStore.LoadSettings(), GuiStore.LoadState());
		main.Activate();
	}
}

// The UI thread's dispatcher, captured at startup so background work
// (Shell pipes, timers) can hand results back without blocking.
internal static class UiThread {
	private static DispatcherQueue? queue;

	public static void Initialize() {
		queue = DispatcherQueue.GetForCurrentThread();
	}

	public static void Post(Action action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal) {
		if (queue is null || !queue.TryEnqueue(priority, () => action())) {
			// No UI (shutting down): run inline rather than lose it.
			action();
		}
	}
}

internal static class Diagnostics {
	private static readonly SemaphoreSlim LogGate = new(1, 1);
	private static readonly SemaphoreSlim DialogGate = new(1, 1);

	// Set by MainWindow: where message dialogs appear.
	public static Func<XamlRoot?>? DialogRoot { get; set; }

	// Appends to gui.log in the background; shows a dialog unless quiet.
	// Safe from any thread.
	public static void Report(string context, Exception exception, bool quiet = false) {
		_ = WriteLogAsync($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {context}: {exception}{Environment.NewLine}");

		if (!quiet) {
			UiThread.Post(() => ShowAsync(context, exception.Message).Forget("Showing a message"), DispatcherQueuePriority.Low);
		}
	}

	public static async Task ShowAsync(string title, string message) {
		if (DialogRoot?.Invoke() is not { } root) {
			return;
		}

		// Only one dialog can be open at a time.
		await DialogGate.WaitAsync();

		try {
			var dialog = new ContentDialog {
				Title = title,
				Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
				CloseButtonText = "OK",
				XamlRoot = root
			};

			await dialog.ShowAsync();
		}
		finally {
			DialogGate.Release();
		}
	}

	private static async Task WriteLogAsync(string entry) {
		try {
			await LogGate.WaitAsync();

			try {
				GuiStore.EnsureFolder();
				await File.AppendAllTextAsync(Path.Combine(GuiStore.Folder, "gui.log"), entry);
			}
			finally {
				LogGate.Release();
			}
		}
		catch {
			// Logging must never throw.
		}
	}
}

internal static class TaskExtensions {
	// Runs a task without awaiting it, reporting (quietly) if it fails, so
	// nothing is left unobserved. Use for fire-and-forget UI actions.
	public static async void Forget(this Task task, string context = "Background task") {
		try {
			await task;
		}
		catch (OperationCanceledException) {
			// Expected when something closes.
		}
		catch (Exception exception) {
			Diagnostics.Report(context, exception, quiet: true);
		}
	}
}

// Paths baked in at build time from Directory.Build.props (AssemblyMetadata).
internal static class BuildDefaults {
	public static string? ShellProject => Metadata("AlexanderShellProject");

	private static string? Metadata(string key) {
		return typeof(BuildDefaults).Assembly
			.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(attribute => attribute.Key == key)?.Value is { Length: > 0 } value
				? value
				: null;
	}
}

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

using Alexander.Hosting;

using G = Alexander.Graphics;

namespace Alexander.Gui;

internal sealed record CommandResult(string Action, string? Message, bool Failed);

// One Shell process, launched with host. The Shell does all the real
// work - building and loading Core, running commands, /rebuild, service
// views - and this just talks to it over stdin/stdout.
//
// Nothing here blocks the caller: the process starts on the thread pool,
// both pipes are read on background tasks, writes are async, and every
// event is posted to the UI thread, so handlers can touch controls.
internal sealed class ShellSession : IAsyncDisposable {
	// Shells start one at a time, each holding the gate until its Core
	// build finishes: restoring several command windows would otherwise
	// launch several builds of the same project at once.
	private static readonly SemaphoreSlim StartGate = new(1, 1);
	private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

	private readonly SemaphoreSlim writeGate = new(1, 1);
	private readonly ConcurrentDictionary<long, TaskCompletionSource<HostMessage>> pending = new();
	private readonly CancellationTokenSource lifetime = new();
	private readonly TaskCompletionSource startup = new(TaskCreationOptions.RunContinuationsAsynchronously);

	private Process? process;
	private StreamWriter? input;
	private long nextId;
	private volatile bool exited;

	// All raised on the UI thread.
	public event Action<string, bool>? Output;               // text, isError
	public event Action<string?, string?, bool>? Progress;   // stage, text, isError
	public event Action<string>? Echo;                       // a command a service view ran
	public event Action<SessionState>? StateChanged;
	public event Action<long, string?, IReadOnlyList<G.DrawOp>>? Frame;   // view instance, title, ops
	public event Action<long, string?>? ViewClosed;          // view instance, reason
	public event Action<int?>? Exited;                       // exit code, if known

	public SessionState? State { get; private set; }

	// True once the Shell has reported Core ready (and until it exits).
	public bool IsReady { get; private set; }

	public bool IsRunning => process is not null && !exited;

	// Launches the Shell and waits (asynchronously) until it reports Core
	// ready or failed, or exits. Metrics and theme go first so service
	// views lay out correctly from their first frame.
	public async Task StartAsync(string shellPath, string launchFolder, G.TextMetrics metrics, string theme) {
		await StartGate.WaitAsync(lifetime.Token);

		try {
			var startInfo = CreateStartInfo(shellPath, launchFolder);

			try {
				process = await Task.Run(() => Process.Start(startInfo), lifetime.Token)
					?? throw new InvalidOperationException("The Shell process didn't start.");
			}
			catch (Win32Exception exception) {
				throw new InvalidOperationException($"Couldn't start the Shell ({startInfo.FileName}): {exception.Message}");
			}

			input = process.StandardInput;
			input.NewLine = "\n";
			input.AutoFlush = false;

			_ = Task.Run(() => ReadOutputAsync(process.StandardOutput));
			_ = Task.Run(() => ReadErrorsAsync(process.StandardError));
			_ = MonitorExitAsync(process);

			await SendAsync(new HostMessage {
				Type = HostMessageTypes.Init,
				Version = HostProtocol.Version,
				Metrics = metrics,
				Theme = theme
			});

			// Hold the gate through this Shell's build; stop waiting (not the
			// Shell) after a generous while.
			await Task.WhenAny(startup.Task, Task.Delay(TimeSpan.FromMinutes(5), lifetime.Token));
		}
		finally {
			StartGate.Release();
		}
	}

	public async Task<CommandResult> RunAsync(string text) {
		var reply = await RequestAsync(new HostMessage { Type = HostMessageTypes.Input, Text = text });

		return reply.Type == HostMessageTypes.Error
			? new CommandResult("continue", reply.Text, Failed: true)
			: new CommandResult(reply.Action ?? "continue", reply.Text, Failed: false);
	}

	// Empty on any problem (or if the Shell is slow): completion is a
	// convenience, never worth an error.
	public async Task<IReadOnlyList<string>> CompleteAsync(string text) {
		if (!IsReady) {
			return [];
		}

		try {
			var reply = await RequestAsync(new HostMessage { Type = HostMessageTypes.Complete, Text = text })
				.WaitAsync(TimeSpan.FromSeconds(2));

			return reply.Items ?? [];
		}
		catch {
			return [];
		}
	}

	public Task SendInitAsync(G.TextMetrics metrics, string theme) {
		return IsRunning
			? SendAsync(new HostMessage { Type = HostMessageTypes.Init, Version = HostProtocol.Version, Metrics = metrics, Theme = theme })
			: Task.CompletedTask;
	}

	public Task OpenViewAsync(long instance, string viewId, double width, double height) {
		return SendAsync(new HostMessage {
			Type = HostMessageTypes.OpenView,
			ViewInstance = instance,
			ViewId = viewId,
			Width = width,
			Height = height
		});
	}

	public Task ResizeViewAsync(long instance, double width, double height) {
		return SendAsync(new HostMessage { Type = HostMessageTypes.ResizeView, ViewInstance = instance, Width = width, Height = height });
	}

	public Task ViewInputAsync(long instance, string kind, double x, double y, string? button, double delta, string? key, G.KeyModifiers modifiers) {
		return SendAsync(new HostMessage {
			Type = HostMessageTypes.ViewInput,
			ViewInstance = instance,
			Text = kind,
			X = x,
			Y = y,
			Button = button,
			Delta = delta,
			Key = key,
			Modifiers = (int)modifiers
		});
	}

	public Task CloseViewAsync(long instance) {
		return IsRunning
			? SendAsync(new HostMessage { Type = HostMessageTypes.CloseView, ViewInstance = instance })
			: Task.CompletedTask;
	}

	// Asks the Shell to stop (it disposes Core cleanly), closes its stdin,
	// and only kills it if it hasn't exited after a few seconds.
	public async ValueTask DisposeAsync() {
		if (process is { } running && !exited) {
			try {
				await SendAsync(new HostMessage { Type = HostMessageTypes.Shutdown }).WaitAsync(TimeSpan.FromSeconds(1));
				input?.Close();
			}
			catch {
				// Already going away.
			}

			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

			try {
				await running.WaitForExitAsync(timeout.Token);
			}
			catch (OperationCanceledException) {
				try {
					running.Kill(entireProcessTree: true);
				}
				catch {
					// Exited in the meantime.
				}
			}
		}

		lifetime.Cancel();
		process?.Dispose();
	}

	// --- process -----------------------------------------------------

	private static ProcessStartInfo CreateStartInfo(string shellPath, string launchFolder) {
		var startInfo = new ProcessStartInfo {
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = launchFolder,
			StandardInputEncoding = Utf8,
			StandardOutputEncoding = Utf8,
			StandardErrorEncoding = Utf8
		};

		switch (Path.GetExtension(shellPath).ToLowerInvariant()) {
			case ".csproj":
				startInfo.FileName = "dotnet";
				startInfo.ArgumentList.Add("run");
				startInfo.ArgumentList.Add("--project");
				startInfo.ArgumentList.Add(shellPath);
				startInfo.ArgumentList.Add("--");
				break;

			case ".dll":
				startInfo.FileName = "dotnet";
				startInfo.ArgumentList.Add(shellPath);
				break;

			// The apphost: Alexander.Shell.exe on Windows, Alexander.Shell elsewhere.
			default:
				startInfo.FileName = shellPath;
				break;
		}

		startInfo.ArgumentList.Add(HostProtocol.HostArgument);
		return startInfo;
	}

	private async Task MonitorExitAsync(Process running) {
		try {
			await running.WaitForExitAsync();
		}
		catch {
			// Disposed.
		}

		int? code = null;

		try {
			code = running.ExitCode;
		}
		catch {
			// Not available.
		}

		exited = true;
		startup.TrySetResult();

		foreach (var waiter in pending.Values) {
			waiter.TrySetException(new InvalidOperationException("The Shell exited."));
		}

		pending.Clear();

		UiThread.Post(() => {
			IsReady = false;
			Exited?.Invoke(code);
		});
	}

	// --- reading -----------------------------------------------------

	private async Task ReadOutputAsync(StreamReader reader) {
		try {
			while (await reader.ReadLineAsync(lifetime.Token) is { } line) {
				if (HostProtocol.TryParse(line) is { } message) {
					Handle(message);
				} else if (line.Length > 0) {
					// Not the protocol - e.g. "dotnet run" building the
					// Shell itself before it takes over stdout.
					UiThread.Post(() => Progress?.Invoke(null, line, false));
				}
			}
		}
		catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException) {
			// Closing.
		}
	}

	private async Task ReadErrorsAsync(StreamReader reader) {
		try {
			while (await reader.ReadLineAsync(lifetime.Token) is { } line) {
				if (line.Length > 0) {
					UiThread.Post(() => Output?.Invoke(line, true));
				}
			}
		}
		catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException) {
			// Closing.
		}
	}

	// Background thread: replies complete their waiting request directly;
	// everything else is posted to the UI thread.
	private void Handle(HostMessage message) {
		if (message.Id != 0 && pending.TryRemove(message.Id, out var waiter)) {
			waiter.TrySetResult(message);
			return;
		}

		switch (message.Type) {
			case HostMessageTypes.Progress:
				if (message.Stage is HostStages.Ready or HostStages.Failed) {
					startup.TrySetResult();
				}

				UiThread.Post(() => {
					if (message.Stage == HostStages.Ready) {
						IsReady = true;
					}

					Progress?.Invoke(message.Stage, message.Text, message.IsError);
				});
				break;

			case HostMessageTypes.Output:
				UiThread.Post(() => Output?.Invoke(message.Text ?? "", message.IsError));
				break;

			case HostMessageTypes.Echo:
				UiThread.Post(() => Echo?.Invoke(message.Text ?? ""));
				break;

			case HostMessageTypes.State when message.State is { } state:
				UiThread.Post(() => {
					State = state;
					StateChanged?.Invoke(state);
				});
				break;

			case HostMessageTypes.Frame:
				UiThread.Post(() => Frame?.Invoke(message.ViewInstance, message.Text, message.Ops ?? []));
				break;

			case HostMessageTypes.ViewClosed:
				UiThread.Post(() => ViewClosed?.Invoke(message.ViewInstance, message.Text));
				break;

			// Results of commands a service view ran (no request id).
			case HostMessageTypes.Result or HostMessageTypes.Error when !string.IsNullOrEmpty(message.Text):
				UiThread.Post(() => Output?.Invoke(
					message.Text!,
					message.Type == HostMessageTypes.Error || message.Text!.StartsWith("Error:", StringComparison.Ordinal)));
				break;
		}
	}

	// --- writing -----------------------------------------------------

	private async Task<HostMessage> RequestAsync(HostMessage message) {
		message.Id = Interlocked.Increment(ref nextId);

		var waiter = new TaskCompletionSource<HostMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
		pending[message.Id] = waiter;

		try {
			await SendAsync(message);
		}
		catch {
			pending.TryRemove(message.Id, out _);
			throw;
		}

		return await waiter.Task;
	}

	private async Task SendAsync(HostMessage message) {
		if (input is null || exited) {
			throw new InvalidOperationException("The Shell isn't running.");
		}

		var line = HostProtocol.Serialize(message);

		await writeGate.WaitAsync(lifetime.Token);

		try {
			await input.WriteLineAsync(line.AsMemory(), lifetime.Token);
			await input.FlushAsync(lifetime.Token);
		}
		finally {
			writeGate.Release();
		}
	}
}

#if DEBUG
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipes;

using Avalonia.Threading;

namespace DriftBuster.Gui.Services;

/// <summary>
/// Named pipe RPC server for GUI automation.
/// Listens on <c>DriftBuster-Automation-{PID}</c> for newline-delimited JSON commands.
/// Single client at a time; reconnects automatically when the client disconnects.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class AutomationServer : IDisposable
{
    internal const string UiDispatchTimeoutMsVariable = "DRIFTBUSTER_AUTOMATION_UI_DISPATCH_TIMEOUT_MS";
    internal static readonly TimeSpan DefaultUiDispatchTimeout = TimeSpan.FromSeconds(30);

    private readonly AutomationDispatcher _dispatcher;
    private readonly string _pipeName;
    private readonly TimeSpan _uiDispatchTimeout;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;
    private bool _disposed;

    public AutomationServer(AutomationDispatcher dispatcher, TimeSpan? uiDispatchTimeout = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _pipeName = $"DriftBuster-Automation-{Environment.ProcessId}";
        _uiDispatchTimeout = uiDispatchTimeout ?? ParseUiDispatchTimeout(Environment.GetEnvironmentVariable(UiDispatchTimeoutMsVariable));
    }

    public string PipeName => _pipeName;

    public void Start()
    {
        if (_listenTask is not null)
        {
            return;
        }

        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        DebugLog.Trace("AutomationServer", "Started", new { PipeName = _pipeName });
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                DebugLog.Trace("AutomationServer", "Client connected");

                await HandleClientAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                DebugLog.Trace("AutomationServer", "ListenLoop error", new { Error = ex.Message });
            }
            finally
            {
                if (pipe is not null)
                {
                    try
                    {
                        if (pipe.IsConnected)
                        {
                            pipe.Disconnect();
                        }
                    }
                    catch
                    {
                        // Best-effort disconnect.
                    }

                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            DebugLog.Trace("AutomationServer", "Command received", new { Command = trimmed });

            if (!await ProcessCommandAsync(trimmed, writer, ct).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    private async Task<bool> ProcessCommandAsync(string command, StreamWriter writer, CancellationToken ct)
    {
        var response = await BuildResponseAsync(command, ct).ConfigureAwait(false);
        if (response is null)
        {
            return false;
        }

        DebugLog.Trace("AutomationServer", "Response sent", new { Response = response });

        try
        {
            await writer.WriteLineAsync(response.AsMemory(), ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> BuildResponseAsync(string command, CancellationToken ct)
    {
        try
        {
            return await DispatchWithTimeoutAsync(
                async () => await Dispatcher.UIThread.InvokeAsync(() => _dispatcher.Dispatch(command)),
                _uiDispatchTimeout,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            return AutomationState.Serialize(AutomationState.ErrorResponse($"Dispatch error: {ex.Message}"));
        }
    }

    internal static async Task<string> DispatchWithTimeoutAsync(
        Func<Task<string>> dispatchAsync,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (dispatchAsync is null)
        {
            throw new ArgumentNullException(nameof(dispatchAsync));
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be greater than zero.");
        }

        var dispatchTask = dispatchAsync();
        var timeoutTask = Task.Delay(timeout, ct);
        var completedTask = await Task.WhenAny(dispatchTask, timeoutTask).ConfigureAwait(false);

        if (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        if (completedTask == dispatchTask)
        {
            return await dispatchTask.ConfigureAwait(false);
        }

        return AutomationState.Serialize(AutomationState.ErrorResponse(
            $"UI thread dispatch timed out after {timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s."));
    }

    internal static TimeSpan ParseUiDispatchTimeout(string? candidate)
    {
        if (int.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds) &&
            milliseconds > 0)
        {
            return TimeSpan.FromMilliseconds(milliseconds);
        }

        return DefaultUiDispatchTimeout;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();

        try
        {
            _listenTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Shutdown timeout — acceptable.
        }

        _cts.Dispose();
        DebugLog.Trace("AutomationServer", "Disposed");
    }
}
#endif

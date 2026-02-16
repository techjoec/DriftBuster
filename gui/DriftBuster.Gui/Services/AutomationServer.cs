#if DEBUG
using System.Diagnostics.CodeAnalysis;
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
    private readonly AutomationDispatcher _dispatcher;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;
    private bool _disposed;

    public AutomationServer(AutomationDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _pipeName = $"DriftBuster-Automation-{Environment.ProcessId}";
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

            string response;
            try
            {
                response = await Dispatcher.UIThread.InvokeAsync(() => _dispatcher.Dispatch(trimmed));
            }
            catch (Exception ex)
            {
                response = AutomationState.Serialize(AutomationState.ErrorResponse($"Dispatch error: {ex.Message}"));
            }

            DebugLog.Trace("AutomationServer", "Response sent", new { Response = response });

            try
            {
                await writer.WriteLineAsync(response.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                break;
            }
        }
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

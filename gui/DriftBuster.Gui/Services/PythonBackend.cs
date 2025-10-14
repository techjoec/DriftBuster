using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DriftBuster.Gui.Services
{
    internal sealed class PythonBackend : IAsyncDisposable, IDisposable
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly SemaphoreSlim _mutex = new(1, 1);
        private readonly Timer _idleTimer;
        private readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(3);

        private Process? _process;
        private StreamWriter? _stdin;
        private StreamReader? _stdout;
        private bool _disposed;

        public PythonBackend()
        {
            _idleTimer = new Timer(OnIdleTimeout, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public async Task<string> SendAsync(object payload, CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();
            await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureProcessAsync(cancellationToken).ConfigureAwait(false);
                ResetIdleTimer();

                var json = JsonSerializer.Serialize(payload, SerializerOptions);
                await _stdin!.WriteLineAsync(json).ConfigureAwait(false);
                await _stdin.FlushAsync().ConfigureAwait(false);

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = await _stdout!.ReadLineAsync().ConfigureAwait(false);
                    if (line is null)
                    {
                        await RestartProcessAsync().ConfigureAwait(false);
                        throw new InvalidOperationException("Backend closed unexpectedly.");
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("ok", out var okElement) || !okElement.GetBoolean())
                    {
                        var message = root.TryGetProperty("error", out var errorElement)
                            ? errorElement.GetString()
                            : "Backend error";
                        throw new InvalidOperationException(message ?? "Backend error");
                    }

                    if (!root.TryGetProperty("result", out var resultElement))
                    {
                        ResetIdleTimer();
                        return "null";
                    }

                    var resultJson = resultElement.GetRawText();
                    ResetIdleTimer();
                    return resultJson;
                }
            }
            finally
            {
                _mutex.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            await _mutex.WaitAsync().ConfigureAwait(false);
            try
            {
                await SendShutdownAsync().ConfigureAwait(false);
                DisposeProcess();
            }
            finally
            {
                _mutex.Release();
                _idleTimer.Dispose();
            }
        }

        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private Task EnsureProcessAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_process is { HasExited: false })
            {
                return Task.CompletedTask;
            }

            DisposeProcess();

            var psi = new ProcessStartInfo
            {
                FileName = "python",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add("driftbuster.api_server");

            var process = Process.Start(psi);
            if (process is null)
            {
                throw new InvalidOperationException("Failed to launch python backend.");
            }

            _process = process;
            _stdin = process.StandardInput;
            _stdout = process.StandardOutput;

            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    Debug.WriteLine($"[DriftBuster backend] {args.Data}");
                }
            };
            process.BeginErrorReadLine();

            ResetIdleTimer();
            return Task.CompletedTask;
        }

        private async Task RestartProcessAsync()
        {
            DisposeProcess();
            await EnsureProcessAsync(CancellationToken.None).ConfigureAwait(false);
        }

        private void DisposeProcess()
        {
            try
            {
                if (_process is { HasExited: false })
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(2000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                Debug.WriteLine($"[DriftBuster backend] dispose: {ex.Message}");
            }

            _process?.Dispose();
            _stdin?.Dispose();
            _stdout?.Dispose();

            _process = null;
            _stdin = null;
            _stdout = null;
        }

        private async Task SendShutdownAsync()
        {
            if (_process is null || _stdin is null || _stdout is null || _process.HasExited)
            {
                return;
            }

            try
            {
                var payload = JsonSerializer.Serialize(new { cmd = "shutdown" }, SerializerOptions);
                await _stdin.WriteLineAsync(payload).ConfigureAwait(false);
                await _stdin.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DriftBuster backend] shutdown failed: {ex.Message}");
            }
        }

        private void ResetIdleTimer()
        {
            _idleTimer.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
        }

        private void OnIdleTimeout(object? state)
        {
            if (!_mutex.Wait(0))
            {
                return;
            }

            try
            {
                DisposeProcess();
            }
            finally
            {
                _mutex.Release();
            }
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PythonBackend));
            }
        }
    }
}

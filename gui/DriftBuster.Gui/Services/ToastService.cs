using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.Input;

namespace DriftBuster.Gui.Services
{
    public enum ToastLevel
    {
        Info,
        Success,
        Warning,
        Error,
    }

    public sealed record ToastAction(string Label, Func<Task> Callback, bool CloseOnInvoke = true);

    public sealed class ToastNotification
    {
        private readonly Action<Guid> _dismiss;
        private readonly ToastAction? _primaryAction;
        private readonly ToastAction? _secondaryAction;

        internal ToastNotification(
            Guid id,
            string title,
            string message,
            ToastLevel level,
            TimeSpan duration,
            ToastAction? primaryAction,
            ToastAction? secondaryAction,
            Action<Guid> dismiss)
        {
            Id = id;
            Title = title;
            Message = message;
            Level = level;
            Duration = duration;
            Timestamp = DateTimeOffset.UtcNow;
            _primaryAction = primaryAction;
            _secondaryAction = secondaryAction;
            _dismiss = dismiss;
            DismissCommand = new RelayCommand(() => _dismiss(Id));
            if (_primaryAction is not null)
            {
                PrimaryCommand = new AsyncRelayCommand(ExecutePrimaryAsync);
            }

            if (_secondaryAction is not null)
            {
                SecondaryCommand = new AsyncRelayCommand(ExecuteSecondaryAsync);
            }
        }

        public Guid Id { get; }

        public string Title { get; }

        public string Message { get; }

        public ToastLevel Level { get; }

        public TimeSpan Duration { get; }

        public DateTimeOffset Timestamp { get; }

        public IRelayCommand DismissCommand { get; }

        public IAsyncRelayCommand? PrimaryCommand { get; }

        public IAsyncRelayCommand? SecondaryCommand { get; }

        public string? PrimaryLabel => _primaryAction?.Label;

        public string? SecondaryLabel => _secondaryAction?.Label;

        private async Task ExecutePrimaryAsync()
        {
            if (_primaryAction is null)
            {
                return;
            }

            await _primaryAction.Callback().ConfigureAwait(false);
            if (_primaryAction.CloseOnInvoke)
            {
                _dismiss(Id);
            }
        }

        private async Task ExecuteSecondaryAsync()
        {
            if (_secondaryAction is null)
            {
                return;
            }

            await _secondaryAction.Callback().ConfigureAwait(false);
            if (_secondaryAction.CloseOnInvoke)
            {
                _dismiss(Id);
            }
        }
    }

    public interface IToastService
    {
        ReadOnlyObservableCollection<ToastNotification> ActiveToasts { get; }

        ToastNotification Show(
            string title,
            string message,
            ToastLevel level,
            TimeSpan? duration = null,
            ToastAction? primaryAction = null,
            ToastAction? secondaryAction = null);

        void Dismiss(Guid id);

        void DismissAll();
    }

    public sealed class ToastService : IToastService
    {
        private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(5);

        private readonly ObservableCollection<ToastNotification> _toasts = new();
        private readonly Dictionary<Guid, CancellationTokenSource> _tokens = new();
        private readonly ReadOnlyObservableCollection<ToastNotification> _readonlyToasts;
        private readonly Action<Action> _dispatcher;

        public ToastService(Action<Action>? dispatcher = null)
        {
            _dispatcher = dispatcher ?? DispatchToUi;
            _readonlyToasts = new ReadOnlyObservableCollection<ToastNotification>(_toasts);
        }

        public ReadOnlyObservableCollection<ToastNotification> ActiveToasts => _readonlyToasts;

        public ToastNotification Show(
            string title,
            string message,
            ToastLevel level,
            TimeSpan? duration = null,
            ToastAction? primaryAction = null,
            ToastAction? secondaryAction = null)
        {
            var toast = new ToastNotification(
                Guid.NewGuid(),
                title,
                message,
                level,
                duration ?? DefaultDuration,
                primaryAction,
                secondaryAction,
                Dismiss);

            _dispatcher(() =>
            {
                _toasts.Add(toast);
                var cts = new CancellationTokenSource();
                _tokens[toast.Id] = cts;
                _ = AutoDismissAsync(toast, cts.Token);
            });

            return toast;
        }

        public void Dismiss(Guid id)
        {
            _dispatcher(() =>
            {
                if (_tokens.Remove(id, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                }

                for (var i = 0; i < _toasts.Count; i++)
                {
                    if (_toasts[i].Id == id)
                    {
                        _toasts.RemoveAt(i);
                        break;
                    }
                }
            });
        }

        public void DismissAll()
        {
            _dispatcher(() =>
            {
                foreach (var token in _tokens.Values)
                {
                    token.Cancel();
                    token.Dispose();
                }
                _tokens.Clear();
                _toasts.Clear();
            });
        }

        private static void DispatchToUi(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.UIThread.Post(action);
            }
        }

        private async Task AutoDismissAsync(ToastNotification toast, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(toast.Duration, cancellationToken).ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested)
                {
                    Dismiss(toast.Id);
                }
            }
            catch (TaskCanceledException)
            {
                // Swallow cancellation; toast was dismissed manually.
            }
        }
    }
}

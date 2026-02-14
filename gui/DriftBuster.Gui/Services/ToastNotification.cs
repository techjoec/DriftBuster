using System;
using System.Globalization;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Input;

namespace DriftBuster.Gui.Services
{
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

        public string TimestampText => Timestamp.ToLocalTime().ToString("t", CultureInfo.InvariantCulture);

        public string LevelLabel => Level.ToString();

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
}

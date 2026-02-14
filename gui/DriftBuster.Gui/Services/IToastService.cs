using System;
using System.Collections.ObjectModel;

namespace DriftBuster.Gui.Services
{
    public interface IToastService
    {
        ReadOnlyObservableCollection<ToastNotification> ActiveToasts { get; }

        ReadOnlyObservableCollection<ToastNotification> OverflowToasts { get; }

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
}

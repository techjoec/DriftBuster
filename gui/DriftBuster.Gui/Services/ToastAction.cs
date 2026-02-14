using System;
using System.Threading.Tasks;

namespace DriftBuster.Gui.Services
{
    public sealed record ToastAction(string Label, Func<Task> Callback, bool CloseOnInvoke = true);
}

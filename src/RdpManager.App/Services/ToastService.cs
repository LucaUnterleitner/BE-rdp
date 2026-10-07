using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using RdpManager.App.ViewModels;

namespace RdpManager.App.Services;

public sealed class Toast(string message, string kind, ICommand dismiss)
{
    public string Message { get; } = message;
    public string Kind { get; } = kind;
    public string Icon => Kind == "info" ? "info" : "checkCircle";
    public ICommand DismissCommand { get; } = dismiss;
}

/// <summary>Short confirmations of completed actions (bottom right). Errors always use dialogs instead.</summary>
public sealed class ToastService
{
    public ObservableCollection<Toast> Items { get; } = [];

    public void Show(string message, string kind = "success", int timeoutMs = 5000)
    {
        Toast? toast = null;
        var dismiss = new RelayCommand(() => { if (toast is not null) Items.Remove(toast); });
        toast = new Toast(message, kind, dismiss);
        Items.Add(toast);
        while (Items.Count > 4) Items.RemoveAt(0);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(timeoutMs) };
        timer.Tick += (_, _) => { timer.Stop(); Items.Remove(toast); };
        timer.Start();
    }
}

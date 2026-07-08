using Allegro.Admin.Models;

namespace Allegro.Admin.Services;

public class ToastService
{
    public event Action? OnChanged;
    public List<ToastMessage> Toasts { get; private set; } = new();

    public void ShowSuccess(string message) => Show(message, ToastType.Success );
    public void ShowError(string message) => Show(message, ToastType.Error);
    public void ShowInfo(string message) => Show(message, ToastType.Info);

    private void Show(string message, ToastType toastType)
    {
        var toast = new ToastMessage { Message = message, CssClass = toastType.CssClass() };
        Toasts.Add(toast);
        OnChanged?.Invoke();
        
        Task.Run(async () =>
        {
            await Task.Delay(3000);
            Toasts.Remove(toast);
            OnChanged?.Invoke();
        });
    }

    public void Remove(ToastMessage toast)
    {
        Toasts.Remove(toast);
        OnChanged?.Invoke();
    }
}
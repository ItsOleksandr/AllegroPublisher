namespace Allegro.Admin.Models;

public enum ToastType
{
    Success,
    Error,
    Info
}

public static class ToastExtensions
{
    public static string CssClass(this ToastType toastType) => toastType switch
    {
        ToastType.Success => "bg-success",
        ToastType.Error => "bg-danger",
        ToastType.Info => "bg-info text-dark",
        _ => "bg-info text-dark"
    };
}
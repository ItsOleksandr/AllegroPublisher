namespace Allegro.Admin.Models;

public class ToastMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Message { get; set; } = string.Empty;
    public string CssClass { get; set; } = "bg-success";
}

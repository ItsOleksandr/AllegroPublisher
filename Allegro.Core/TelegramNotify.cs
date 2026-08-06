using System.Text;
using System.Text.Json;

namespace Allegro.Core;

public class TelegramNotify
{
    public async Task<bool> SendAsync(
        string botToken,
        string chatId,
        string? text = null,
        Stream? fileStream = null,
        byte[]? fileBytes = null,
        byte[]? thumbnailBytes = null,
        string? fileName = null,
        object? replyMarkup = null
    )
    {
        using var client = new HttpClient();
        var baseUrl = $"https://api.telegram.org/bot{botToken}/";

        HttpResponseMessage? response = null;
        try
        {
            // 📎 Якщо є файл → sendDocument
            if (fileStream != null || fileBytes != null)
            {
                using var form = new MultipartFormDataContent();

                form.Add(new StringContent(chatId), "chat_id");

                if (!string.IsNullOrEmpty(text))
                    form.Add(new StringContent(text), "caption");

                if (replyMarkup != null)
                {
                    var markupJson = JsonSerializer.Serialize(replyMarkup);
                    form.Add(new StringContent(markupJson, Encoding.UTF8, "application/json"), "reply_markup");
                }

                HttpContent fileContent;

                if (fileStream != null)
                {
                    fileContent = new StreamContent(fileStream);
                }
                else
                {
                    fileContent = new ByteArrayContent(fileBytes!);
                }

                fileContent.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                form.Add(fileContent, "document", fileName ?? "file.bin");

                // 🖼 thumbnail (опціонально)
                if (thumbnailBytes != null)
                {
                    var thumbContent = new ByteArrayContent(thumbnailBytes);
                    thumbContent.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");

                    form.Add(thumbContent, "thumbnail", "thumb.png");
                }

                response = await client.PostAsync(baseUrl + "sendDocument", form);
            }
            else
            {
                // 💬 Звичайне повідомлення
                var payload = new Dictionary<string, object>
                {
                    { "chat_id", chatId },
                    { "text", text ?? string.Empty }
                };

                if (replyMarkup != null)
                    payload["reply_markup"] = replyMarkup;

                var json = JsonSerializer.Serialize(payload);
                response = await client.PostAsync(
                    baseUrl + "sendMessage",
                    new StringContent(json, Encoding.UTF8, "application/json")
                );
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new Exception(error);
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Telegram send failed: {ex}");
            return false;
        }
        finally
        {
            response?.Dispose();
        }
    }
}
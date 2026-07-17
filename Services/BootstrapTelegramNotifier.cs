using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;

namespace PremierAPI.Services;

public static class BootstrapTelegramNotifier
{
    public static async Task<bool> TrySendAsync(
        IConfiguration configuration,
        string message,
        CancellationToken cancellationToken = default)
    {
        string? token = configuration["Telegram:BotToken"];
        string? chatId = configuration["Telegram:ChatId"];
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId))
            return false;

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                $"https://api.telegram.org/bot{token}/sendMessage",
                new { chat_id = chatId, text = $"🚨 PremierHost API [Critical]\n\n{message}" },
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

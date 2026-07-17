using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PremierAPI.Services
{
    public class TelegramLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly string _botToken;
        private readonly string _chatId;
        private readonly HttpClient _httpClient;
        private readonly LogLevel _minimumLevel;

        public TelegramLogger(string categoryName, string botToken, string chatId, HttpClient httpClient, LogLevel minimumLevel)
        {
            _categoryName = categoryName;
            _botToken = botToken;
            _chatId = chatId;
            _httpClient = httpClient;
            _minimumLevel = minimumLevel;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None && logLevel >= _minimumLevel;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message)) return;

            string emoji = logLevel switch
            {
                LogLevel.Critical => "🚨",
                LogLevel.Error => "❌",
                LogLevel.Warning => "⚠️",
                LogLevel.Information => "ℹ️",
                _ => "💬"
            };
            string levelText = logLevel.ToString();

            if (message.StartsWith("[TG-INFO]"))
            {
                message = message.Replace("[TG-INFO]", "").Trim();
                emoji = "ℹ️";
                levelText = "Information";
            }

            string text = $"{emoji} *PremierHost API [{levelText}]*\n" +
                          $"*Cat:* {_categoryName}\n\n" +
                          $"*Msg:* {message}\n";

            if (exception != null)
            {
                text += $"\n*Exception:* {exception.Message}\n";
            }

            _ = SendTelegramMessageAsync(text);
        }

        private async Task SendTelegramMessageAsync(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(_botToken) || string.IsNullOrEmpty(_chatId)) return;

                var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
                var payload = new
                {
                    chat_id = _chatId,
                    text = text,
                    parse_mode = "Markdown"
                };

                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                await _httpClient.PostAsync(url, content);
            }
            catch
            {
                // Ignorar erro do logger para não derrubar a aplicação
            }
        }
    }

    public class TelegramLoggerProvider : ILoggerProvider
    {
        private readonly string _botToken;
        private readonly string _chatId;
        private readonly HttpClient _httpClient;
        private readonly LogLevel _minimumLevel;

        public TelegramLoggerProvider(string botToken, string chatId, LogLevel minimumLevel)
        {
            _botToken = botToken;
            _chatId = chatId;
            _httpClient = new HttpClient();
            _minimumLevel = minimumLevel;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new TelegramLogger(categoryName, _botToken, _chatId, _httpClient, _minimumLevel);
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}

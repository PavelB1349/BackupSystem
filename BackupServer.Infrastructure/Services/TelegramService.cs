using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BackupServer.Infrastructure.Services
{
    public class TelegramService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<TelegramService> _logger;
        private readonly string? _botToken;
        private readonly string? _chatId;

        public TelegramService(HttpClient httpClient, IConfiguration config, ILogger<TelegramService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;

            // Проверяем оба варианта ключа (TelegramSettings и Telegram)
            _botToken = config["TelegramSettings:BotToken"] ?? config["Telegram:BotToken"];
            _chatId = config["TelegramSettings:ChatId"] ?? config["Telegram:ChatId"];
        }

        public async Task SendAlertAsync(string message)
        {
            if (string.IsNullOrEmpty(_botToken) || string.IsNullOrEmpty(_chatId))
            {
                _logger.LogWarning("[Telegram] Ошибка: BotToken или ChatId не заданы в конфигурации!");
                return;
            }

            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            var payload = new
            {
                chat_id = _chatId,
                text = message,
                parse_mode = "HTML"
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.PostAsync(url, content);
                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    _logger.LogError("[Telegram API Error] Код {StatusCode}: {Error}", response.StatusCode, errorBody);
                }
                else
                {
                    _logger.LogInformation("[Telegram] Сообщение успешно отправлено.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Telegram Error] Ошибка соединения с Telegram API");
            }
        }
    }
}
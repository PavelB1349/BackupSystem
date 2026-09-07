using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace BackupServer.Infrastructure.Services
{
    public class TelegramService
    {
        private readonly HttpClient _httpClient;
        private readonly string? _botToken;
        private readonly string? _chatId;

        public TelegramService(HttpClient httpClient, IConfiguration config)
        {
            _httpClient = httpClient;
            _botToken = config["Telegram:BotToken"];
            _chatId = config["Telegram:ChatId"];
        }

        public async Task SendAlertAsync(string message)
        {
            if (string.IsNullOrEmpty(_botToken) || string.IsNullOrEmpty(_chatId))
                return;

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
                await _httpClient.PostAsync(url, content);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Telegram Error] {ex.Message}");
            }
        }
    }
}

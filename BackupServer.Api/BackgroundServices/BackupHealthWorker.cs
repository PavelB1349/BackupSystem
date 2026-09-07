using System.Text;
using BackupServer.Api.Configuration;
using BackupServer.Core.Enums;
using BackupServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupServer.Infrastructure.Services;

public class BackupHealthWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BackupHealthWorker> _logger;
    private readonly TelegramService _telegram;
    private readonly IConfiguration _config;

    private DateTime _lastDailyReportDate = DateTime.MinValue;
    private DateTime _lastOverdueAlertDate = DateTime.MinValue;
    private bool _isDiskAlertActive = false;

    public BackupHealthWorker(
        IServiceProvider serviceProvider,
        ILogger<BackupHealthWorker> logger,
        TelegramService telegram,
        IConfiguration config)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _telegram = telegram;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Пауза 30 секунд при старте для завершения инициализации сервера
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;

                // 1. Проверка места на диске сервера
                await CheckDiskSpaceAsync();

                // 2. Утренний отчет в 09:00
                if (now.Hour == 9 && _lastDailyReportDate.Date != now.Date)
                {
                    await SendDailyReportAsync();
                    _lastDailyReportDate = now;
                }

                // 3. Дневной алерт по просроченным кассам в 15:00
                if (now.Hour == 15 && _lastOverdueAlertDate.Date != now.Date)
                {
                    await SendOverdueAlertAsync();
                    _lastOverdueAlertDate = now;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HealthWorker] Ошибка выполнения проверки состояния бэкапов");
            }

            // Проверка каждые 15 минут
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }

    private async Task CheckDiskSpaceAsync()
    {
        try
        {
            var drive = new DriveInfo("C:\\");
            long freeGb = drive.AvailableFreeSpace / 1024 / 1024 / 1024;

            if (freeGb < 15 && !_isDiskAlertActive)
            {
                _isDiskAlertActive = true;
                await _telegram.SendAlertAsync($"🚨 <b>КРИТИЧЕСКАЯ УГРОЗА!</b>\n\nЗаканчивается место на диске C:\\\nОсталось свободно: <b>{freeGb} ГБ</b>.\nПрием бэкапов под угрозой!");
            }
            else if (freeGb >= 15 && _isDiskAlertActive)
            {
                _isDiskAlertActive = false;
                await _telegram.SendAlertAsync($"✅ <b>Место на диске освобождено.</b> Доступно: {freeGb} ГБ.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[HealthWorker] Не удалось проверить диск: {ex.Message}");
        }
    }

    private async Task SendOverdueAlertAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var thresholdOverdue = DateTime.Now.AddDays(-DynamicSettings.OverdueDays);

        // Анализируем ТОЛЬКО АКТИВНЫЕ кассы (IsActive == true)
        var activePoints = await db.Points
            .Include(p => p.ExchangeOffice)
            .ThenInclude(e => e.City)
            .Where(p => p.IsActive)
            .ToListAsync();

        var overdueList = new List<string>();

        foreach (var point in activePoints)
        {
            var latestLog = await db.BackupLogs
                .Where(b => b.PointId == point.Id)
                .OrderByDescending(b => b.FileCreatedAt)
                .FirstOrDefaultAsync();

            if (latestLog == null || latestLog.FileCreatedAt < thresholdOverdue || latestLog.Status != BackupStatus.Success)
            {
                string lastDateText = latestLog != null ? latestLog.FileCreatedAt.ToString("dd.MM HH:mm") : "Никогда";
                overdueList.Add($"• <b>{point.ExchangeOffice.City.Name} / {point.ExchangeOffice.Name} ({point.Code})</b> — Посл. копия: {lastDateText}");
            }
        }

        if (overdueList.Any())
        {
            var msg = new StringBuilder($"🟡 <b>ВНИМАНИЕ: Просрочка бэкапов (> {DynamicSettings.OverdueDays} дн.)</b>\n\n");
            foreach (var item in overdueList.Take(20))
            {
                msg.AppendLine(item);
            }

            if (overdueList.Count > 20)
            {
                msg.AppendLine($"\n<i>...и еще {overdueList.Count - 20} касс. Посмотреть все можно в дашборде.</i>");
            }

            await _telegram.SendAlertAsync(msg.ToString());
        }
    }

    private async Task SendDailyReportAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var yesterday = DateTime.Now.AddDays(-1);

        int activePointsCount = await db.Points.CountAsync(p => p.IsActive);
        int success24h = await db.BackupLogs.CountAsync(l => l.FileCreatedAt >= yesterday && l.Status == BackupStatus.Success);
        int corrupted24h = await db.BackupLogs.CountAsync(l => l.FileCreatedAt >= yesterday && l.Status == BackupStatus.Corrupted);

        long freeGb = 0;
        try { freeGb = new DriveInfo("C:\\").AvailableFreeSpace / 1024 / 1024 / 1024; } catch { }

        await _telegram.SendAlertAsync(
            $"📊 <b>Утренняя сводка бэкапов</b>\n\n" +
            $"📡 Активных касс на мониторинге: <b>{activePointsCount}</b>\n" +
            $"🟢 Успешных копий за 24ч: <b>{success24h}</b>\n" +
            $"🔴 Битых архивов за 24ч: <b>{corrupted24h}</b>\n" +
            $"💾 Свободно на сервере: <b>{freeGb} ГБ</b>"
        );
    }
}
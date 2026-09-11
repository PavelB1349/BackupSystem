using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BackupServer.Api.Configuration;
using BackupServer.Core.Enums;
using BackupServer.Infrastructure.Persistence;
using BackupServer.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupServer.Api.BackgroundServices;

public class BackupHealthWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BackupHealthWorker> _logger;
    private readonly TelegramService _telegram;

    private DateTime _lastDailyReportDate = DateTime.MinValue;
    private DateTime _lastEveningReportDate = DateTime.MinValue;
    private DateTime _lastOverdueAlertDate = DateTime.MinValue;

    public BackupHealthWorker(
        IServiceProvider serviceProvider,
        ILogger<BackupHealthWorker> logger,
        TelegramService telegram)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _telegram = telegram;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = TimeHelper.GetKazakhstanTime();

                // Утренний отчет в 09:00
                if (now.Hour == 9 && _lastDailyReportDate.Date != now.Date)
                {
                    await SendSummaryReportAsync("📊 <b>Утренняя сводка бэкапов</b>");
                    _lastDailyReportDate = now.Date;
                }

                // Алерт по просрочкам в 15:00
                if (now.Hour == 15 && _lastOverdueAlertDate.Date != now.Date)
                {
                    await SendOverdueAlertAsync();
                    _lastOverdueAlertDate = now.Date;
                }

                // Вечерний отчет в 22:00
                if (now.Hour == 22 && _lastEveningReportDate.Date != now.Date)
                {
                    await SendSummaryReportAsync("🌆 <b>Вечерняя сводка бэкапов</b>");
                    _lastEveningReportDate = now.Date;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HealthWorker] Ошибка при проверке состояния бэкапов");
            }

            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }

    private async Task SendOverdueAlertAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var thresholdOverdue = TimeHelper.GetKazakhstanTime().AddDays(-DynamicSettings.OverdueDays);

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
                msg.AppendLine($"\n<i>...и еще {overdueList.Count - 20} касс.</i>");
            }
            msg.AppendLine($"\n🔗 <a href=\"http://136.119.233.111:5000\">Открыть Дашборд Мониторинга</a>");

            await _telegram.SendAlertAsync(msg.ToString());
        }
    }

    private async Task SendSummaryReportAsync(string title)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var currentTime = TimeHelper.GetKazakhstanTime();
        var thresholdOverdue = currentTime.AddDays(-DynamicSettings.OverdueDays);
        var yesterday = currentTime.AddDays(-1);

        var activePoints = await db.Points.Where(p => p.IsActive).ToListAsync();
        int activePointsCount = activePoints.Count;

        int freshBackupsCount = 0;
        int overdueCount = 0;

        foreach (var point in activePoints)
        {
            var latestLog = await db.BackupLogs
                .Where(b => b.PointId == point.Id)
                .OrderByDescending(b => b.FileCreatedAt)
                .FirstOrDefaultAsync();

            if (latestLog != null && latestLog.Status == BackupStatus.Success && latestLog.FileCreatedAt >= thresholdOverdue)
            {
                freshBackupsCount++;
            }
            else
            {
                overdueCount++;
            }
        }

        int corrupted24h = await db.BackupLogs.CountAsync(l => l.FileCreatedAt >= yesterday && l.Status == BackupStatus.Corrupted);

        await _telegram.SendAlertAsync(
            $"{title}\n\n" +
            $"📡 Активных касс на мониторинге: <b>{activePointsCount}</b>\n" +
            $"🟢 С актуальными копиями: <b>{freshBackupsCount}</b>\n" +
            $"🟡 Требуют внимания (просрочка): <b>{overdueCount}</b>\n" +
            $"🔴 Битых архивов за 24ч: <b>{corrupted24h}</b>"
        );
    }
}
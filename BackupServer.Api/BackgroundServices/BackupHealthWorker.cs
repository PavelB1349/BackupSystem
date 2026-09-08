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

                // Утренний отчет в 09:00 по времени Казахстана
                if (now.Hour == 9 && _lastDailyReportDate.Date != now.Date)
                {
                    await SendDailyReportAsync();
                    _lastDailyReportDate = now.Date;
                }

                // Алерт по просрочкам в 15:00 по времени Казахстана
                if (now.Hour == 15 && _lastOverdueAlertDate.Date != now.Date)
                {
                    await SendOverdueAlertAsync();
                    _lastOverdueAlertDate = now.Date;
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

        var thresholdOverdue = DateTime.Now.AddDays(-DynamicSettings.OverdueDays);

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

        await _telegram.SendAlertAsync(
            $"📊 <b>Утренняя сводка бэкапов</b>\n\n" +
            $"📡 Активных касс на мониторинге: <b>{activePointsCount}</b>\n" +
            $"🟢 Успешных копий за 24ч: <b>{success24h}</b>\n" +
            $"🔴 Битых архивов за 24ч: <b>{corrupted24h}</b>"
        );
    }
}
using System;
using System.Threading;
using System.Threading.Tasks;
using BackupServer.Api.Configuration;
using BackupServer.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupServer.Api.BackgroundServices;

public class BackupRetentionWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BackupRetentionWorker> _logger;

    public BackupRetentionWorker(IServiceProvider serviceProvider, ILogger<BackupRetentionWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var scanner = scope.ServiceProvider.GetRequiredService<FtpScannerService>();

                var (newFiles, newPoints) = await scanner.ScanFtpAsync(stoppingToken);
                int deleted = await scanner.CleanupOldBackupsAsync(stoppingToken);

                if (newFiles > 0 || deleted > 0)
                {
                    _logger.LogInformation($"[Worker] Сканирование завершено: +{newFiles} файлов, -{deleted} устаревших.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Worker] Ошибка при выполнении фоновой задачи FTP");
            }

            int currentInterval = Math.Max(1, DynamicSettings.ScanIntervalMinutes);
            await Task.Delay(TimeSpan.FromMinutes(currentInterval), stoppingToken);
        }
    }
}
using BackupServer.Api.Configuration;
using BackupServer.Infrastructure.Persistence;
using FluentFTP;
using Microsoft.EntityFrameworkCore;

namespace BackupServer.Api.BackgroundServices;

public class BackupRetentionWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BackupRetentionWorker> _logger;
    private readonly IConfiguration _config;

    public BackupRetentionWorker(IServiceProvider serviceProvider, ILogger<BackupRetentionWorker> logger, IConfiguration config)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { ExecuteRetentionCleanup(); }
            catch (Exception ex) { _logger.LogError(ex, "Ошибка при выполнении ротации бэкапов"); }

            // Запуск каждые 6 часов
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private void ExecuteRetentionCleanup()
    {
        int maxBackups = DynamicSettings.MaxBackupsPerPoint;
        string ftpHost = _config["FtpSettings:Host"] ?? "ftp.a8pro.kz";
        string ftpUser = _config["FtpSettings:User"] ?? "A8pro";
        string ftpPass = Environment.GetEnvironmentVariable("FTP_PASSWORD") ?? _config["FtpSettings:Password"] ?? "";

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var points = db.Points.ToList();

        foreach (var point in points)
        {
            // Берем ВСЕ бэкапы конкретной точки, отсортированные от свежих к старым
            var pointLogs = db.BackupLogs
                .Where(b => b.PointId == point.Id)
                .OrderByDescending(b => b.FileCreatedAt)
                .ToList();

            // Если у точки больше чем maxBackups (например, больше 3)
            if (pointLogs.Count > maxBackups)
            {
                var logsToDelete = pointLogs.Skip(maxBackups).ToList();

                using var ftpClient = new FtpClient(ftpHost, ftpUser, ftpPass);
                try { ftpClient.Connect(); } catch { continue; }

                foreach (var log in logsToDelete)
                {
                    // Удаляем лишний файл с FTP
                    if (!string.IsNullOrEmpty(log.FilePath))
                    {
                        try { if (ftpClient.FileExists(log.FilePath)) ftpClient.DeleteFile(log.FilePath); } catch { }
                    }

                    // Удаляем из базы данных
                    db.BackupLogs.Remove(log);
                }

                db.SaveChanges();
                ftpClient.Disconnect();
                _logger.LogInformation($"[Retention] Удалено {logsToDelete.Count} старых бэкапов для точки ID {point.Id}. Оставлено {maxBackups} последних.");
            }
        }
    }
}
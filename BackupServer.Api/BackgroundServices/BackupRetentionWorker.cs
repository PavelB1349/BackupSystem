using BackupServer.Api.Configuration;
using BackupServer.Core.Entities;
using BackupServer.Core.Enums;
using BackupServer.Infrastructure.Persistence;
using FluentFTP;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BackupServer.Api.BackgroundServices
{
    public class BackupRetentionWorker : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _config;
        private readonly ILogger<BackupRetentionWorker> _logger;

        public BackupRetentionWorker(
            IServiceProvider serviceProvider,
            IConfiguration config,
            ILogger<BackupRetentionWorker> logger)
        {
            _serviceProvider = serviceProvider;
            _config = config;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Запуск фонового сканирования и ротации FTP...");

                try
                {
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                        await PerformScanAndRetentionAsync(db);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка при выполнении фоновой задачи FTP");
                }

                // ⏱️ Интервал запуска (по умолчанию 1 час)
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }

        private async Task PerformScanAndRetentionAsync(AppDbContext db)
        {
            string ftpHost = _config["FtpSettings:Host"] ?? "ftp.a8pro.kz";
            string ftpUser = _config["FtpSettings:User"] ?? "A8pro";
            string ftpPass = Environment.GetEnvironmentVariable("FTP_PASSWORD") ?? _config["FtpSettings:Password"] ?? "";
            string rootFolder = _config["FtpSettings:RootFolder"] ?? "Backups_V2";

            int newFilesFound = 0;
            int deletedFilesCount = 0;

            using (var ftp = new FtpClient(ftpHost, ftpUser, ftpPass))
            {
                ftp.Encoding = Encoding.GetEncoding("windows-1251");
                ftp.Connect();

                string targetFolder = ftp.DirectoryExists(rootFolder) ? rootFolder : ".";
                var items = ftp.GetListing(targetFolder, FtpListOption.Recursive | FtpListOption.Modify);

                // --- 1. СКАНИРОВАНИЕ И АВТО-СОЗДАНИЕ КАСС ---
                foreach (var item in items)
                {
                    if (item.Type != FtpObjectType.File || !item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool exists = await db.BackupLogs.AnyAsync(b => b.FileName == item.Name);
                    if (exists) continue;

                    var parts = Path.GetFileNameWithoutExtension(item.Name).Split('_');
                    if (parts.Length >= 2)
                    {
                        string officeName = parts[0];
                        string pointCode = parts[1];

                        // Извлекаем город из пути FTP
                        string detectedCityName = "Алматы";
                        var pathSegments = item.FullName.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                        int rootIndex = Array.FindIndex(pathSegments, s => s.Equals("Backups_V2", StringComparison.OrdinalIgnoreCase));
                        if (rootIndex >= 0 && pathSegments.Length > rootIndex + 1)
                        {
                            detectedCityName = pathSegments[rootIndex + 1];
                        }

                        var point = await db.Points
                            .Include(p => p.ExchangeOffice)
                            .FirstOrDefaultAsync(p => p.Code == pointCode && p.ExchangeOffice.Name == officeName);

                        if (point == null)
                        {
                            var city = await db.Cities.FirstOrDefaultAsync(c => c.Name == detectedCityName);
                            if (city == null)
                            {
                                city = new City { Name = detectedCityName };
                                db.Cities.Add(city);
                                await db.SaveChangesAsync();
                            }

                            var office = await db.ExchangeOffices.FirstOrDefaultAsync(e => e.Name == officeName);
                            if (office == null)
                            {
                                office = new ExchangeOffice
                                {
                                    Name = officeName,
                                    CityId = city.Id
                                };
                                db.ExchangeOffices.Add(office);
                                await db.SaveChangesAsync();
                            }

                            point = new Point
                            {
                                Code = pointCode,
                                ExchangeOfficeId = office.Id,
                                IsActive = true
                            };
                            db.Points.Add(point);
                            await db.SaveChangesAsync();
                        }

                        // Парсинг точной даты из имени файла
                        DateTime fileDate = DateTime.Now;
                        if (parts.Length >= 4 && DateTime.TryParseExact(
                            $"{parts[2]}_{parts[3]}",
                            "yyyy-MM-dd_HHmmss",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var parsedDate))
                        {
                            fileDate = parsedDate;
                        }
                        else if (item.Modified != DateTime.MinValue)
                        {
                            fileDate = item.Modified.ToLocalTime();
                        }

                        var log = new BackupLog
                        {
                            PointId = point.Id,
                            FileName = item.Name,
                            FilePath = item.FullName,
                            FileSizeBytes = item.Size,
                            FileCreatedAt = fileDate,
                            ProcessedAt = DateTime.Now,
                            Status = BackupStatus.Success
                        };

                        db.BackupLogs.Add(log);
                        newFilesFound++;
                    }
                }

                if (newFilesFound > 0)
                {
                    await db.SaveChangesAsync();
                    _logger.LogInformation($"[Авто-сканер] Добавлено новых бэкапов: {newFilesFound}");
                }

                // --- 2. РОТАЦИЯ СТАРОГО БЭКАПА ---
                int maxBackups = DynamicSettings.MaxBackupsPerPoint;
                var points = await db.Points.ToListAsync();

                foreach (var p in points)
                {
                    var logs = await db.BackupLogs
                        .Where(b => b.PointId == p.Id)
                        .OrderByDescending(b => b.FileCreatedAt)
                        .ToListAsync();

                    if (logs.Count > maxBackups)
                    {
                        var logsToDelete = logs.Skip(maxBackups).ToList();
                        foreach (var log in logsToDelete)
                        {
                            if (!string.IsNullOrEmpty(log.FilePath))
                            {
                                try
                                {
                                    ftp.DeleteFile(log.FilePath);
                                    deletedFilesCount++;
                                }
                                catch
                                {
                                    // Игнорируем если файл физически отсутствует
                                }
                            }

                            db.BackupLogs.Remove(log);
                        }
                    }
                }

                if (deletedFilesCount > 0 || db.ChangeTracker.HasChanges())
                {
                    await db.SaveChangesAsync();
                    _logger.LogInformation($"[Авто-ротация] Удалено устаревших бэкапов: {deletedFilesCount}");
                }

                ftp.Disconnect();
            }
        }
    }
}
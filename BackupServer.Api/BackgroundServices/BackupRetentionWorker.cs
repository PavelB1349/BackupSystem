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

            _logger.LogInformation("[Worker] Сервис автосканирования и ротации запущен.");
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation($"[Worker] Запуск фонового сканирования и ротации FTP ({DateTime.Now:HH:mm:ss})...");

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
                    _logger.LogError(ex, "[Worker] Ошибка при выполнении фоновой задачи FTP");
                }

                // ⚡ Динамически считываем интервал при каждом вызове задержки
                int currentInterval = Math.Max(1, DynamicSettings.ScanIntervalMinutes);
                _logger.LogInformation($"[Worker] Следующее сканирование через {currentInterval} мин.");

                await Task.Delay(TimeSpan.FromMinutes(currentInterval), stoppingToken);
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

                // =======================================================
                // ШАГ 1: СКАНИРОВАНИЕ, АВТО-СОЗДАНИЕ И ОБНОВЛЕНИЕ СУБД
                // =======================================================
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

                        // 🛢️ Проверяем наличие метки СУБД в названии файла (PG или SQL)
                        bool hasDbTag = parts.Length >= 5 && (parts[2].Equals("PG", StringComparison.OrdinalIgnoreCase) || parts[2].Equals("SQL", StringComparison.OrdinalIgnoreCase));
                        bool isPg = hasDbTag && parts[2].Equals("PG", StringComparison.OrdinalIgnoreCase);

                        // 🏙️ Извлекаем город из пути FTP
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
                                IsActive = true,
                                DbType = isPg ? DatabaseType.PostgreSql : DatabaseType.MsSql
                            };
                            db.Points.Add(point);
                            await db.SaveChangesAsync();
                        }
                        else if (hasDbTag)
                        {
                            // ⚡ Актуализируем СУБД существующей кассы, если пришел файл с тегом PG/SQL
                            var detectedDbType = isPg ? DatabaseType.PostgreSql : DatabaseType.MsSql;
                            if (point.DbType != detectedDbType)
                            {
                                point.DbType = detectedDbType;
                            }
                        }

                        // 🕒 Умный парсинг даты (учитывает смещение индексов из-за метки СУБД)
                        DateTime fileDate = DateTime.Now;
                        string datePart = hasDbTag ? parts[3] : (parts.Length >= 3 ? parts[2] : "");
                        string timePart = hasDbTag ? parts[4] : (parts.Length >= 4 ? parts[3] : "");

                        if (!string.IsNullOrEmpty(datePart) && !string.IsNullOrEmpty(timePart) &&
                            DateTime.TryParseExact($"{datePart}_{timePart}", "yyyy-MM-dd_HHmmss",
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out var parsedDate))
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

                // =======================================================
                // ШАГ 2: РОТАЦИЯ СТАРОГО БЭКАПА
                // =======================================================
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
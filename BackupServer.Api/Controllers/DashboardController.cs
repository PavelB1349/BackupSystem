using BackupServer.Api.Configuration;
using BackupServer.Api.DTOs;
using BackupServer.Core.Enums;
using BackupServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackupServer.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public DashboardController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    [HttpGet("stats")]
    public async Task<ActionResult<DashboardStatsDto>> GetStats()
    {
        var now = DateTime.Now;
        var thresholdOverdue = now.AddDays(-DynamicSettings.OverdueDays);
        var thresholdClosed = now.AddDays(-DynamicSettings.ClosedDays);

        var activePoints = await _db.Points.Where(p => p.IsActive).ToListAsync();
        int totalPoints = activePoints.Count;

        int backupsToday = 0;
        int missingToday = 0;
        int errorsToday = 0;

        foreach (var point in activePoints)
        {
            var latestLog = await _db.BackupLogs
                .Where(b => b.PointId == point.Id)
                .OrderByDescending(b => b.FileCreatedAt)
                .FirstOrDefaultAsync();

            if (latestLog == null)
            {
                missingToday++;
            }
            else if (latestLog.FileCreatedAt < thresholdClosed)
            {
                // Закрытая точка
            }
            else if (latestLog.Status != BackupStatus.Success)
            {
                errorsToday++;
            }
            else if (latestLog.FileCreatedAt >= thresholdOverdue)
            {
                backupsToday++;
            }
            else
            {
                missingToday++;
            }
        }

        return Ok(new DashboardStatsDto(totalPoints, backupsToday, missingToday, errorsToday));
    }

    [HttpGet("points")]
    public async Task<ActionResult<IEnumerable<PointStatusDto>>> GetPointsStatus()
    {
        var now = DateTime.Now;
        var thresholdOverdue = now.AddDays(-DynamicSettings.OverdueDays);
        var thresholdClosed = now.AddDays(-DynamicSettings.ClosedDays);

        var points = await _db.Points
            .Include(p => p.ExchangeOffice)
            .ThenInclude(e => e.City)
            .Where(p => p.IsActive)
            .ToListAsync();

        var result = new List<PointStatusDto>();

        foreach (var point in points)
        {
            var latestLog = await _db.BackupLogs
                .Where(b => b.PointId == point.Id)
                .OrderByDescending(b => b.FileCreatedAt)
                .FirstOrDefaultAsync();

            string status = "Missing";

            if (latestLog != null)
            {
                if (latestLog.FileCreatedAt < thresholdClosed)
                {
                    status = "Closed";
                }
                else if (latestLog.Status != BackupStatus.Success)
                {
                    status = "Error";
                }
                else if (latestLog.FileCreatedAt >= thresholdOverdue)
                {
                    status = "Success";
                }
                else
                {
                    status = "Missing";
                }
            }

            result.Add(new PointStatusDto(
                point.Id,
                point.ExchangeOffice.City.Name,
                point.ExchangeOffice.Name,
                point.Code,
                latestLog?.FileCreatedAt,
                latestLog?.FileSizeBytes,
                status,
                point.IsActive,
                point.DbType.ToString() // 🛢️ Передаём СУБД ("MsSql" или "PostgreSql")
            ));
        }

        return Ok(result);
    }

    [HttpGet("logs")]
    public async Task<ActionResult<IEnumerable<BackupLogDto>>> GetLogs(
        [FromQuery] string? city,
        [FromQuery] string? office,
        [FromQuery] string? pointCode,
        [FromQuery] string? status)
    {
        var query = _db.BackupLogs
            .Include(b => b.Point)
            .ThenInclude(p => p.ExchangeOffice)
            .ThenInclude(e => e.City)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(city))
            query = query.Where(b => b.Point.ExchangeOffice.City.Name == city);

        if (!string.IsNullOrWhiteSpace(office))
            query = query.Where(b => b.Point.ExchangeOffice.Name == office);

        if (!string.IsNullOrWhiteSpace(pointCode))
            query = query.Where(b => b.Point.Code == pointCode);

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<BackupStatus>(status, true, out var parsedStatus))
        {
            query = query.Where(b => b.Status == parsedStatus);
        }

        var logs = await query
            .OrderByDescending(b => b.FileCreatedAt)
            .Take(100)
            .Select(b => new BackupLogDto(
                b.Id,
                b.Point.ExchangeOffice.City.Name,
                b.Point.ExchangeOffice.Name,
                b.Point.Code,
                b.FileName,
                b.FileSizeBytes,
                b.FileCreatedAt,
                b.ProcessedAt,
                b.Status.ToString(),
                b.ErrorMessage,
                b.Point.DbType.ToString() // 🛢️ Передаём СУБД ("MsSql" или "PostgreSql")
            ))
            .ToListAsync();

        return Ok(logs);
    }

    // 🛢️ PUT: api/dashboard/points/{id}/dbtype (СМЕНА СУБД ДЛЯ КАССЫ)
    [HttpPut("points/{id}/dbtype")]
    public async Task<IActionResult> UpdatePointDbType(int id, [FromBody] DatabaseType dbType)
    {
        var point = await _db.Points.FindAsync(id);
        if (point == null) return NotFound(new { Message = "Касса не найдена" });

        point.DbType = dbType;
        await _db.SaveChangesAsync();

        return Ok(new { Message = $"СУБД для кассы успешно изменена на {dbType}" });
    }

    // POST: api/dashboard/scan (СКАНИРОВАНИЕ С ОПРЕДЕЛЕНИЕМ ГОРОДА И СУБД)
    [HttpPost("scan")]
    public async Task<IActionResult> ForceScan()
    {
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            string ftpHost = _config["FtpSettings:Host"] ?? "ftp.a8pro.kz";
            string ftpUser = _config["FtpSettings:User"] ?? "A8pro";
            string ftpPass = Environment.GetEnvironmentVariable("FTP_PASSWORD") ?? _config["FtpSettings:Password"] ?? "";
            string rootFolder = _config["FtpSettings:RootFolder"] ?? "Backups_V2";

            int scannedFilesCount = 0;
            int newFilesFound = 0;
            int autoCreatedPointsCount = 0;

            using (var ftp = new FluentFTP.FtpClient(ftpHost, ftpUser, ftpPass))
            {
                ftp.Encoding = System.Text.Encoding.GetEncoding("windows-1251");
                ftp.Connect();

                string targetFolder = ftp.DirectoryExists(rootFolder) ? rootFolder : ".";
                var items = ftp.GetListing(targetFolder, FluentFTP.FtpListOption.Recursive | FluentFTP.FtpListOption.Modify);

                foreach (var item in items)
                {
                    if (item.Type != FluentFTP.FtpObjectType.File || !item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        continue;

                    scannedFilesCount++;

                    bool exists = await _db.BackupLogs.AnyAsync(b => b.FileName == item.Name);
                    if (exists) continue;

                    var parts = Path.GetFileNameWithoutExtension(item.Name).Split('_');
                    if (parts.Length >= 2)
                    {
                        string officeName = parts[0]; // PGTest
                        string pointCode = parts[1];  // OP1

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

                        // Ищем точку в базе
                        var point = await _db.Points
                            .Include(p => p.ExchangeOffice)
                            .FirstOrDefaultAsync(p => p.Code == pointCode && p.ExchangeOffice.Name == officeName);

                        // ⚡ АВТО-СОЗДАНИЕ
                        if (point == null)
                        {
                            var city = await _db.Cities.FirstOrDefaultAsync(c => c.Name == detectedCityName);
                            if (city == null)
                            {
                                city = new BackupServer.Core.Entities.City { Name = detectedCityName };
                                _db.Cities.Add(city);
                                await _db.SaveChangesAsync();
                            }

                            var office = await _db.ExchangeOffices
                                .FirstOrDefaultAsync(e => e.Name == officeName);

                            if (office == null)
                            {
                                office = new BackupServer.Core.Entities.ExchangeOffice
                                {
                                    Name = officeName,
                                    CityId = city.Id
                                };
                                _db.ExchangeOffices.Add(office);
                                await _db.SaveChangesAsync();
                            }

                            point = new BackupServer.Core.Entities.Point
                            {
                                Code = pointCode,
                                ExchangeOfficeId = office.Id,
                                IsActive = true,
                                DbType = isPg ? DatabaseType.PostgreSql : DatabaseType.MsSql
                            };
                            _db.Points.Add(point);
                            await _db.SaveChangesAsync();
                            autoCreatedPointsCount++;
                        }
                        else if (hasDbTag)
                        {
                            // ⚡ ОБНОВЛЯЕМ СУБД существующей кассы при поступлении файла с тегом PG/SQL
                            var detectedDbType = isPg ? DatabaseType.PostgreSql : DatabaseType.MsSql;
                            if (point.DbType != detectedDbType)
                            {
                                point.DbType = detectedDbType;
                            }
                        }

                        // 🕒 Парсинг даты (учитывает наличие/отсутствие метки СУБД)
                        DateTime fileDate = DateTime.Now;
                        string datePart = hasDbTag ? parts[3] : (parts.Length >= 3 ? parts[2] : "");
                        string timePart = hasDbTag ? parts[4] : (parts.Length >= 4 ? parts[3] : "");

                        if (!string.IsNullOrEmpty(datePart) && !string.IsNullOrEmpty(timePart) &&
                            DateTime.TryParseExact($"{datePart}_{timePart}", "yyyy-MM-dd_HHmmss",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out var parsedDate))
                        {
                            fileDate = parsedDate;
                        }
                        else if (item.Modified != DateTime.MinValue)
                        {
                            fileDate = item.Modified.ToLocalTime();
                        }

                        var log = new BackupServer.Core.Entities.BackupLog
                        {
                            PointId = point.Id,
                            FileName = item.Name,
                            FilePath = item.FullName,
                            FileSizeBytes = item.Size,
                            FileCreatedAt = fileDate,
                            ProcessedAt = DateTime.Now,
                            Status = BackupStatus.Success
                        };

                        _db.BackupLogs.Add(log);
                        newFilesFound++;
                    }
                }

                if (newFilesFound > 0 || _db.ChangeTracker.HasChanges())
                {
                    await _db.SaveChangesAsync();
                }

                ftp.Disconnect();
            }

            string message = $"Сканирование завершено. Новых бэкапов: {newFilesFound}.";
            if (autoCreatedPointsCount > 0)
            {
                message += $" Зарегистрировано новых касс: {autoCreatedPointsCount}.";
            }

            return Ok(new { Message = message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = $"Ошибка сканирования FTP: {ex.Message}" });
        }
    }

    // POST: api/dashboard/cleanup
    [HttpPost("cleanup")]
    public async Task<IActionResult> CleanupOldBackups()
    {
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            string ftpHost = _config["FtpSettings:Host"] ?? "ftp.a8pro.kz";
            string ftpUser = _config["FtpSettings:User"] ?? "A8pro";
            string ftpPass = Environment.GetEnvironmentVariable("FTP_PASSWORD") ?? _config["FtpSettings:Password"] ?? "";

            int maxBackups = DynamicSettings.MaxBackupsPerPoint;
            int deletedFilesCount = 0;

            using (var ftp = new FluentFTP.FtpClient(ftpHost, ftpUser, ftpPass))
            {
                ftp.Encoding = System.Text.Encoding.GetEncoding("windows-1251");
                ftp.Connect();

                var points = await _db.Points.ToListAsync();

                foreach (var point in points)
                {
                    var logs = await _db.BackupLogs
                        .Where(b => b.PointId == point.Id)
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
                                    // Файл физически отсутствует на FTP
                                }
                            }

                            _db.BackupLogs.Remove(log);
                        }
                    }
                }

                if (deletedFilesCount > 0 || _db.ChangeTracker.HasChanges())
                {
                    await _db.SaveChangesAsync();
                }

                ftp.Disconnect();
            }

            return Ok(new { Message = $"Ротация завершена. Удалено старых бэкапов с FTP: {deletedFilesCount}" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = $"Ошибка очистки FTP: {ex.Message}" });
        }
    }

    [HttpGet("settings")]
    public ActionResult<SettingsDto> GetSettings()
    {
        return Ok(new SettingsDto(
            DynamicSettings.OverdueDays,
            DynamicSettings.ClosedDays,
            DynamicSettings.MaxBackupsPerPoint,
            DynamicSettings.ScanIntervalMinutes
        ));
    }

    [HttpPost("settings")]
    public IActionResult SaveSettings([FromBody] SettingsDto dto)
    {
        DynamicSettings.OverdueDays = Math.Max(1, dto.OverdueDays);
        DynamicSettings.ClosedDays = Math.Max(1, dto.ClosedDays);
        DynamicSettings.MaxBackupsPerPoint = Math.Max(1, dto.MaxBackupsPerPoint);
        DynamicSettings.ScanIntervalMinutes = Math.Max(1, dto.ScanIntervalMinutes);

        return Ok(new { Message = "Настройки успешно сохранены!" });
    }
}
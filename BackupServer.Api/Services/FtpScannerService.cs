using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BackupServer.Api.Configuration;
using BackupServer.Core.Entities;
using BackupServer.Core.Enums;
using BackupServer.Infrastructure.Persistence;
using FluentFTP;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BackupServer.Api.Services;

public class FtpScannerService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<FtpScannerService> _logger;

    public FtpScannerService(AppDbContext db, IConfiguration config, ILogger<FtpScannerService> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    public async Task<(int newFiles, int autoPoints)> ScanFtpAsync(CancellationToken token = default)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        string ftpHost = _config["FtpSettings:Host"] ?? "ftp.a8pro.kz";
        string ftpUser = _config["FtpSettings:User"] ?? "A8pro";
        string ftpPass = Environment.GetEnvironmentVariable("FTP_PASSWORD") ?? _config["FtpSettings:Password"] ?? "";
        string rootFolder = _config["FtpSettings:RootFolder"] ?? "Backups_V2";

        int newFilesFound = 0;
        int autoCreatedPointsCount = 0;

        // 1. Обычный синхронный вызов FTP (как у тебя и работало)
        using var ftp = new FtpClient(ftpHost, ftpUser, ftpPass);
        ftp.Encoding = Encoding.GetEncoding("windows-1251");
        ftp.Connect();

        string targetFolder = ftp.DirectoryExists(rootFolder) ? rootFolder : ".";
        var items = ftp.GetListing(targetFolder, FtpListOption.Recursive | FtpListOption.Modify);

        // 2. Асинхронная работа с БД
        foreach (var item in items)
        {
            if (token.IsCancellationRequested) break;

            if (item.Type != FtpObjectType.File || !item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                continue;

            bool exists = await _db.BackupLogs.AnyAsync(b => b.FileName == item.Name, token);
            if (exists) continue;

            var parts = Path.GetFileNameWithoutExtension(item.Name).Split('_');
            if (parts.Length < 2) continue;

            string officeName = parts[0];
            string pointCode = parts[1];

            bool hasDbTag = parts.Length >= 5 && (parts[2].Equals("PG", StringComparison.OrdinalIgnoreCase) || parts[2].Equals("SQL", StringComparison.OrdinalIgnoreCase));
            bool isPg = hasDbTag && parts[2].Equals("PG", StringComparison.OrdinalIgnoreCase);

            string detectedCityName = ExtractCityName(item.FullName, rootFolder);

            var point = await _db.Points
                .Include(p => p.ExchangeOffice)
                .FirstOrDefaultAsync(p => p.Code == pointCode && p.ExchangeOffice.Name == officeName, token);

            if (point == null)
            {
                var city = await _db.Cities.FirstOrDefaultAsync(c => c.Name == detectedCityName, token) ?? new City { Name = detectedCityName };
                if (city.Id == 0) _db.Cities.Add(city);

                var office = await _db.ExchangeOffices.FirstOrDefaultAsync(e => e.Name == officeName, token) ?? new ExchangeOffice { Name = officeName, City = city };
                if (office.Id == 0) _db.ExchangeOffices.Add(office);

                point = new Point { Code = pointCode, ExchangeOffice = office, IsActive = true, DbType = isPg ? DatabaseType.PostgreSql : DatabaseType.MsSql };
                _db.Points.Add(point);
                autoCreatedPointsCount++;
            }
            else if (hasDbTag)
            {
                var detectedDbType = isPg ? DatabaseType.PostgreSql : DatabaseType.MsSql;
                if (point.DbType != detectedDbType) point.DbType = detectedDbType;
            }

            var log = new BackupLog
            {
                Point = point,
                FileName = item.Name,
                FilePath = item.FullName,
                FileSizeBytes = item.Size,
                FileCreatedAt = ParseFileDate(parts, hasDbTag),
                ProcessedAt = TimeHelper.GetKazakhstanTime(), // ⚡ Привязка к KZ-времени
                Status = BackupStatus.Success
            };

            _db.BackupLogs.Add(log);
            newFilesFound++;
        }

        if (newFilesFound > 0 || autoCreatedPointsCount > 0)
        {
            await _db.SaveChangesAsync(token);
        }

        ftp.Disconnect();
        return (newFilesFound, autoCreatedPointsCount);
    }

    public async Task<int> CleanupOldBackupsAsync(CancellationToken token = default)
    {
        string ftpHost = _config["FtpSettings:Host"] ?? "ftp.a8pro.kz";
        string ftpUser = _config["FtpSettings:User"] ?? "A8pro";
        string ftpPass = Environment.GetEnvironmentVariable("FTP_PASSWORD") ?? _config["FtpSettings:Password"] ?? "";

        int maxBackups = DynamicSettings.MaxBackupsPerPoint;
        int deletedFilesCount = 0;

        using var ftp = new FtpClient(ftpHost, ftpUser, ftpPass);
        ftp.Encoding = Encoding.GetEncoding("windows-1251");
        ftp.Connect();

        var points = await _db.Points.ToListAsync(token);

        foreach (var point in points)
        {
            var logs = await _db.BackupLogs
                .Where(b => b.PointId == point.Id)
                .OrderByDescending(b => b.FileCreatedAt)
                .ToListAsync(token);

            if (logs.Count > maxBackups)
            {
                var logsToDelete = logs.Skip(maxBackups).ToList();
                foreach (var log in logsToDelete)
                {
                    if (!string.IsNullOrEmpty(log.FilePath))
                    {
                        try { ftp.DeleteFile(log.FilePath); deletedFilesCount++; }
                        catch { }
                    }
                    _db.BackupLogs.Remove(log);
                }
            }
        }

        if (deletedFilesCount > 0)
        {
            await _db.SaveChangesAsync(token);
        }

        ftp.Disconnect();
        return deletedFilesCount;
    }

    private string ExtractCityName(string fullPath, string rootFolder)
    {
        var pathSegments = fullPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        int rootIndex = Array.FindIndex(pathSegments, s => s.Equals(rootFolder, StringComparison.OrdinalIgnoreCase));
        if (rootIndex >= 0 && pathSegments.Length > rootIndex + 1)
        {
            return pathSegments[rootIndex + 1];
        }
        return "Неизвестный город";
    }

    private DateTime ParseFileDate(string[] parts, bool hasDbTag)
    {
        string datePart = hasDbTag ? parts[3] : (parts.Length >= 3 ? parts[2] : "");
        string timePart = hasDbTag ? parts[4] : (parts.Length >= 4 ? parts[3] : "");
        if (!string.IsNullOrEmpty(datePart) && !string.IsNullOrEmpty(timePart) &&
            DateTime.TryParseExact($"{datePart}_{timePart}", "yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
        {
            return parsedDate;
        }
        return TimeHelper.GetKazakhstanTime();
    }
}
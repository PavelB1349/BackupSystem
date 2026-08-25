using System.IO.Compression;
using System.Text.RegularExpressions;
using BackupServer.Core.Entities;
using BackupServer.Core.Enums;
using BackupServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BackupServer.Infrastructure.Services;

public class FileScannerService
{
    private readonly AppDbContext _context;
    private readonly ILogger<FileScannerService> _logger;

    public FileScannerService(AppDbContext context, ILogger<FileScannerService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task ScanDirectoryAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(rootPath))
        {
            _logger.LogWarning("Папка для сканирования не найдена: {RootPath}", rootPath);
            return;
        }

        var zipFiles = Directory.GetFiles(rootPath, "*.zip", SearchOption.AllDirectories);

        foreach (var filePath in zipFiles)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await ProcessFileAsync(filePath, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task ProcessFileAsync(string filePath, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(filePath);
        var fileName = fileInfo.Name;

        var pathSegments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (pathSegments.Length < 3) return;

        string cityName = pathSegments[^3];
        string officeName = pathSegments[^2];

        var match = Regex.Match(fileName, @"^(.+)_(OP\d+)_((\d{4}-\d{2}-\d{2})_(\d{6}))\.zip$", RegexOptions.IgnoreCase);
        if (!match.Success) return;

        string pointCode = match.Groups[2].Value;

        var city = await _context.Cities.FirstOrDefaultAsync(c => c.Name == cityName, cancellationToken)
                   ?? new City { Name = cityName };

        var office = await _context.ExchangeOffices
            .FirstOrDefaultAsync(o => o.Name == officeName && o.City == city, cancellationToken)
            ?? new ExchangeOffice { Name = officeName, City = city };

        var point = await _context.Points
            .FirstOrDefaultAsync(p => p.Code == pointCode && p.ExchangeOffice == office, cancellationToken)
            ?? new Point { Code = pointCode, ExchangeOffice = office };

        bool exists = await _context.BackupLogs.AnyAsync(l => l.FileName == fileName, cancellationToken);
        if (exists) return;

        var status = BackupStatus.Success;
        string? errorMessage = null;

        if (fileInfo.Length == 0)
        {
            status = BackupStatus.Corrupted;
            errorMessage = "Файл имеет нулевой размер (0 байт)";
        }
        else
        {
            try
            {
                using var zip = ZipFile.OpenRead(filePath);
                _ = zip.Entries.Count;
            }
            catch (Exception ex)
            {
                status = BackupStatus.Corrupted;
                errorMessage = $"Архив поврежден: {ex.Message}";
            }
        }

        var log = new BackupLog
        {
            Point = point,
            FileName = fileName,
            FilePath = filePath,
            FileSizeBytes = fileInfo.Length,
            FileCreatedAt = fileInfo.CreationTimeUtc,
            ProcessedAt = DateTime.UtcNow,
            Status = status,
            ErrorMessage = errorMessage
        };

        _context.BackupLogs.Add(log);
        _logger.LogInformation("Зафиксирован бэкап: {FileName} | Статус: {Status}", fileName, status);
    }
}
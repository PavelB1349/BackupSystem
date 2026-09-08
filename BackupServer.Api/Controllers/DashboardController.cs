using BackupServer.Api.Configuration;
using BackupServer.Api.DTOs;
using BackupServer.Api.Services;
using BackupServer.Core.Enums;
using BackupServer.Infrastructure.Persistence;
using BackupServer.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackupServer.Api.Controllers;

[Authorize]
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
        .ToListAsync();

        var result = new List<PointStatusDto>();
        foreach (var point in points)
        {
            var latestLog = await _db.BackupLogs
                .Where(b => b.PointId == point.Id)
                .OrderByDescending(b => b.FileCreatedAt)
                .FirstOrDefaultAsync();

            string status = "Missing";

            // Если касса отключена вручную
            if (!point.IsActive)
            {
                status = "Disabled";
            }
            else if (latestLog != null)
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
                point.DbType.ToString()
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

    // POST: api/dashboard/scan
    [HttpPost("scan")]
    public async Task<IActionResult> ForceScan([FromServices] FtpScannerService scanner)
    {
        try
        {
            var (newFiles, autoPoints) = await scanner.ScanFtpAsync();
            string message = $"Сканирование завершено. Новых бэкапов: {newFiles}.";
            if (autoPoints > 0) message += $" Зарегистрировано новых касс: {autoPoints}.";

            return Ok(new { Message = message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = $"Ошибка сканирования FTP: {ex.Message}" });
        }
    }

    // POST: api/dashboard/cleanup
    [HttpPost("cleanup")]
    public async Task<IActionResult> CleanupOldBackups([FromServices] FtpScannerService scanner)
    {
        try
        {
            int deletedFilesCount = await scanner.CleanupOldBackupsAsync();
            return Ok(new { Message = $"Ротация завершена. Удалено старых бэкапов с FTP: {deletedFilesCount}" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Message = $"Ошибка очистки FTP: {ex.Message}" });
        }
    }

    // PATCH: api/dashboard/points/{id}/toggle-active
    [HttpPatch("points/{id}/toggle-active")]
    public async Task<IActionResult> TogglePointActive(int id)
    {
        var point = await _db.Points.FindAsync(id);
        if (point == null) return NotFound(new { Message = "Касса не найдена" });

        point.IsActive = !point.IsActive; // Переключаем статус
        await _db.SaveChangesAsync();

        string state = point.IsActive ? "активирована" : "отключена от мониторинга";
        return Ok(new { Message = $"Касса {point.Code} {state}", IsActive = point.IsActive });
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

    // DELETE: api/dashboard/points/{id}
    [HttpDelete("points/{id}")]
    public async Task<IActionResult> DeletePoint(int id)
    {
        var point = await _db.Points
            .Include(p => p.BackupLogs)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (point == null)
            return NotFound(new { Message = $"Касса с ID {id} не найдена в базе" });

        // Удаляем кассу и связанные логи
        _db.BackupLogs.RemoveRange(point.BackupLogs);
        _db.Points.Remove(point);

        await _db.SaveChangesAsync();

        return Ok(new { Message = $"Касса {point.Code} и её история успешно удалены" });
    }

}
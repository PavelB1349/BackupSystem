namespace BackupServer.Api.DTOs;

// Карточки верхушки дашборда
public record DashboardStatsDto(
    int TotalPoints,
    int BackupsToday,
    int MissingToday,
    int ErrorsToday
);

// Элемент таблицы статуса касс (кто сдал / не сдал)
public record PointStatusDto(
    int PointId,
    string CityName,
    string OfficeName,
    string PointCode,
    DateTime? LastBackupTime,
    long? LastFileSize,
    string Status, // "Success", "Missing", "Error"
    bool IsActive
);

// Элемент логирования для детального журнала
public record BackupLogDto(
    long Id,
    string CityName,
    string OfficeName,
    string PointCode,
    string FileName,
    long FileSizeBytes,
    DateTime FileCreatedAt,
    DateTime ProcessedAt,
    string Status,
    string? ErrorMessage
);
public record SettingsDto(
    int OverdueDays,
    int ClosedDays,
    int MaxBackupsPerPoint
);
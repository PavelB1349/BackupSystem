using BackupServer.Core.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace BackupServer.Core.Entities
{
    public class BackupLog
    {
        public long Id { get; set; }
        public int PointId { get; set; }

        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }

        public DateTime FileCreatedAt { get; set; } // Время из имени файла
        public DateTime ProcessedAt { get; set; } = DateTime.UtcNow; // Время проверки сканером

        public BackupStatus Status { get; set; }
        public string? ErrorMessage { get; set; }

        public Point Point { get; set; } = null!;
    }
}

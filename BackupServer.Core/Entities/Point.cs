using BackupServer.Core.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace BackupServer.Core.Entities
{
    public class Point
    {
        public int Id { get; set; }
        public int ExchangeOfficeId { get; set; }
        public string Code { get; set; } = string.Empty; // Например: "OP1", "OP2"

        // Время дедлайна сдачи бэкапа (по умолчанию 22:00)
        public TimeSpan ExpectedDeadline { get; set; } = new TimeSpan(22, 0, 0);
        public bool IsActive { get; set; } = true;

        public ExchangeOffice ExchangeOffice { get; set; } = null!;
        // 🛢️ Поле для хранения СУБД (по умолчанию MSSQL)
        public DatabaseType DbType { get; set; } = DatabaseType.MsSql;
        public ICollection<BackupLog> BackupLogs { get; set; } = new List<BackupLog>();
    }
}

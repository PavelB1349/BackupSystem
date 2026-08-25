using System;
using System.Collections.Generic;
using System.Text;

namespace BackupServer.Core.Enums
{
    public enum BackupStatus
    {
        Success = 1,   // Архив валиден и прилетел вовремя
        Missing = 2,   // Архив не появился до наступления дедлайна
        Corrupted = 3  // Архив поврежден или 0 байт
    }
}

using System;
using System.Runtime.InteropServices;

namespace BackupServer.Api.Configuration;

public static class TimeHelper
{
    public static DateTime GetKazakhstanTime()
    {
        var utcNow = DateTime.UtcNow;
        try
        {
            // Поддержка ИД часового пояса для Linux (Google Cloud) и Windows
            string tzId = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "Central Asia Standard Time"
                : "Asia/Almaty";

            var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
            return TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
        }
        catch
        {
            // Резервный сдвиг UTC+5 для Казахстана
            return utcNow.AddHours(5);
        }
    }
}
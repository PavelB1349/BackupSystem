using System.Text.Json;
using System.Text.Json.Nodes;
using BackupAgent.Helpers;

namespace BackupAgent.Services;

public static class ConfigWizard
{
    public static void Run(string configPath)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=== ПЕРВИЧНАЯ НАСТРОЙКА / СМЕНА ПАРАМЕТРОВ АГЕНТА ===");
        Console.ResetColor();

        string city = ConsoleHelper.ReadRequiredString("Город (например, Алматы): ");
        string office = ConsoleHelper.ReadRequiredString("Название обменника (например, SilkWay): ");
        string pointCode = ConsoleHelper.ReadRequiredString("Код кассы (например, OP1): ");

        Console.Write("\nТип СУБД (1 - MSSQL, 2 - PostgreSQL) [1]: ");
        bool isPg = Console.ReadLine()?.Trim() == "2";
        string dbType = isPg ? "PostgreSql" : "MsSql";

        string dbName = ConsoleHelper.ReadRequiredString($"Имя базы данных 1С [{(isPg ? "ExchangePg" : "Exchange")}]: ");

        string pgUser = "", pgPassEncrypted = "", mssqlConnStr = "", pgDumpPath = "";

        if (isPg)
        {
            Console.Write("Пользователь PostgreSQL [postgres]: ");
            pgUser = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(pgUser)) pgUser = "postgres";

            Console.Write($"Пароль пользователя '{pgUser}': ");
            string pgPass = ConsoleHelper.ReadPasswordSecurely();
            pgPassEncrypted = SecurityService.EncryptSecret(pgPass);

            pgDumpPath = ConsoleHelper.FindPgDumpPath();
            if (string.IsNullOrEmpty(pgDumpPath))
            {
                pgDumpPath = ConsoleHelper.ReadRequiredString("\npg_dump.exe не найден автоматически. Введите полный путь к файлу: ");
            }
            else
            {
                Console.WriteLine($"\n[ОК] Найдена утилита PostgreSQL: {pgDumpPath}");
            }
        }
        else
        {
            Console.Write("\nПароль пользователя 'sa' для MSSQL: ");
            string saPass = ConsoleHelper.ReadPasswordSecurely();
            mssqlConnStr = $"Server=localhost;Database={dbName};User Id=sa;Password={saPass};TrustServerCertificate=True;";
        }

        Console.Write("\nПароль от FTP-сервера: ");
        string ftpPassword = ConsoleHelper.ReadPasswordSecurely();
        string ftpPassEncrypted = SecurityService.EncryptSecret(ftpPassword);

        var jsonConfig = new JsonObject
        {
            ["AgentSettings"] = new JsonObject
            {
                ["DbType"] = dbType,
                ["CityName"] = city,
                ["OfficeName"] = office,
                ["PointCode"] = pointCode,
                ["DatabaseName"] = dbName,
                ["ConnectionString"] = mssqlConnStr,
                ["PgDumpPath"] = pgDumpPath,
                ["PgHost"] = "localhost",
                ["PgPort"] = "5432",
                ["PgUser"] = pgUser,
                ["PgPasswordEncrypted"] = pgPassEncrypted,
                ["FtpHost"] = "ftp.a8pro.kz",
                ["FtpUser"] = "A8pro",
                ["FtpPasswordEncrypted"] = ftpPassEncrypted,
                ["FtpRootFolder"] = "Backups_V2"
            }
        };

        File.WriteAllText(configPath, jsonConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n\n[УСПЕШНО] Конфигурация сохранена и зашифрована!");
        Console.ResetColor();
        Console.WriteLine("Чтобы проверить бэкап прямо сейчас, выполните: BackupAgent.exe --run");
        Console.WriteLine("Чтобы зарегистрировать автозапуск, выполните: BackupAgent.exe --install");
        Console.ReadKey();
    }
}
using System.Diagnostics;
//using System.Diagnostics.EventLog;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentFTP;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

[DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
[DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
const int SW_HIDE = 0;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

string exePath = Process.GetCurrentProcess().MainModule.FileName;
string exeDir = Path.GetDirectoryName(exePath);
string configPath = Path.Combine(exeDir, "appsettings.json");

// =======================================================
// КОМАНДА 1: УСТАНОВКА В ПЛАНИРОВЩИК (--install)
// =======================================================
if (args.Contains("--install"))
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("=== УСТАНОВКА В ПЛАНИРОВЩИК СЛУЖБ WINDOWS ===");
    Console.ResetColor();

    Console.Write("Интервал запуска в часах (1, 2, 3, 6) [по умолчанию 3]: ");
    string hoursInput = Console.ReadLine();
    if (!int.TryParse(hoursInput, out int hours) || hours < 1) hours = 3;

    string taskCommand = $"/create /tn \"A8pro_1C_Backup\" /tr \"\\\"{exePath}\\\"\" /sc HOURLY /mo {hours} /st 00:00 /f /rl HIGHEST";
    var proc = Process.Start(new ProcessStartInfo("schtasks", taskCommand) { UseShellExecute = false });
    proc.WaitForExit();

    if (proc.ExitCode == 0)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n[УСПЕШНО] Задача создана! Запуск каждые {hours} ч. (00:00, {hours:D2}:00, ...)");
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("\n[ОШИБКА] Не удалось создать задачу. Запустите консоль от Администратора!");
    }
    Console.ResetColor();
    Console.ReadKey();
    return;
}

// =======================================================
// КОМАНДА 2: НАСТРОЙКА ИЛИ ПЕРЕНАСТРОЙКА (--config или нет файла)
// =======================================================
if (args.Contains("--config") || !File.Exists(configPath))
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("=== ПЕРВИЧНАЯ НАСТРОЙКА / СМЕНА ПАРАМЕТРОВ АГЕНТА ===");
    Console.ResetColor();

    string city = ReadRequiredString("Город (например, Алматы): ");
    string office = ReadRequiredString("Название обменника (например, SilkWay): ");
    string pointCode = ReadRequiredString("Код кассы (например, OP1): ");

    Console.Write("\nТип СУБД (1 - MSSQL, 2 - PostgreSQL) [1]: ");
    bool isPg = Console.ReadLine()?.Trim() == "2";
    string dbType = isPg ? "PostgreSql" : "MsSql";

    string dbName = ReadRequiredString($"Имя базы данных 1С [{(isPg ? "ExchangePg" : "Exchange")}]: ");

    string pgUser = "", pgPassEncrypted = "", mssqlConnStr = "", pgDumpPath = "";

    if (isPg)
    {
        Console.Write("Пользователь PostgreSQL [postgres]: ");
        pgUser = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(pgUser)) pgUser = "postgres";

        Console.Write($"Пароль пользователя '{pgUser}': ");
        string pgPass = ReadPasswordSecurely();
        pgPassEncrypted = EncryptSecret(pgPass);

        pgDumpPath = FindPgDumpPath();
        if (string.IsNullOrEmpty(pgDumpPath))
        {
            pgDumpPath = ReadRequiredString("\npg_dump.exe не найден автоматически. Введите полный путь к файлу: ");
        }
        else
        {
            Console.WriteLine($"\n[ОК] Найдена утилита PostgreSQL: {pgDumpPath}");
        }
    }
    else
    {
        Console.Write("\nПароль пользователя 'sa' для MSSQL: ");
        string saPass = ReadPasswordSecurely();
        mssqlConnStr = $"Server=localhost;Database={dbName};User Id=sa;Password={saPass};TrustServerCertificate=True;";
    }

    Console.Write("\nПароль от FTP-сервера: ");
    string ftpPassword = ReadPasswordSecurely();
    string ftpPassEncrypted = EncryptSecret(ftpPassword);

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
    Console.WriteLine("Чтобы проверить бэкап прямо сейчас, выполните: dotnet run --project BackupAgent -- --run");
    Console.WriteLine("Чтобы зарегистрировать автозапуск, выполните: BackupAgent.exe --install");
    Console.ReadKey();
    return;
}

// =======================================================
// КОМАНДА 3: БОЕВОЙ ИЛИ ТЕСТОВЫЙ ЗАПУСК
// =======================================================
bool isManualRun = args.Contains("--run");

if (!isManualRun)
{
    ShowWindow(GetConsoleWindow(), SW_HIDE);
}
else
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("=======================================================================");
    Console.WriteLine("  [РУЧНОЙ ЗАПУСК] РЕЗЕРВНОЕ КОПИРОВАНИЕ И ОТПРАВКА НА FTP");
    Console.WriteLine("=======================================================================");
    Console.ResetColor();
}

try
{
    var configuration = new ConfigurationBuilder()
        .SetBasePath(exeDir)
        .AddJsonFile("appsettings.json", optional: false)
        .Build();

    string dbType = configuration["AgentSettings:DbType"] ?? "MsSql";
    string cityName = configuration["AgentSettings:CityName"];
    string officeName = configuration["AgentSettings:OfficeName"];
    string pointCode = configuration["AgentSettings:PointCode"];
    string dbName = configuration["AgentSettings:DatabaseName"];
    string ftpPass = DecryptSecret(configuration["AgentSettings:FtpPasswordEncrypted"]);
    string ftpHost = configuration["AgentSettings:FtpHost"] ?? "ftp.a8pro.kz";
    string ftpUser = configuration["AgentSettings:FtpUser"] ?? "A8pro";
    string ftpRootFolder = configuration["AgentSettings:FtpRootFolder"] ?? "Backups_V2";

    string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
    string tempFolder = @"C:\BackupTemp";
    Directory.CreateDirectory(tempFolder);

    string tempBakPath = Path.Combine(tempFolder, $"{dbName}_{timestamp}.bak");
    string archiveFileName = $"{officeName}_{pointCode}_{timestamp}.zip";
    string tempZipPath = Path.Combine(tempFolder, archiveFileName);

    Console.WriteLine($"\n[1/3] Создание дампа базы ({dbType})...");

    if (dbType.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
    {
        string pgPass = DecryptSecret(configuration["AgentSettings:PgPasswordEncrypted"]);
        string pgDump = configuration["AgentSettings:PgDumpPath"];
        string pgUser = configuration["AgentSettings:PgUser"];

        var startInfo = new ProcessStartInfo
        {
            FileName = pgDump,
            Arguments = $"--host=localhost --port=5432 --username={pgUser} --format=custom --file=\"{tempBakPath}\" {dbName}",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.EnvironmentVariables["PGPASSWORD"] = pgPass;

        using var process = Process.Start(startInfo);
        process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception(process.StandardError.ReadToEnd());
    }
    else
    {
        string connStr = configuration["AgentSettings:ConnectionString"];
        using var connection = new SqlConnection(connStr);
        connection.Open();
        using var command = new SqlCommand($@"BACKUP DATABASE [{dbName}] TO DISK = N'{tempBakPath}' WITH FORMAT, INIT;", connection);
        command.CommandTimeout = 3600;
        command.ExecuteNonQuery();
    }

    // =======================================================
    // ШАГ 2: СЖАТИЕ В ZIP С ПРОГРЕСС-БАРОМ
    // =======================================================
    var bakFileInfo = new FileInfo(tempBakPath);
    long totalBakBytes = bakFileInfo.Length;

    Console.WriteLine($"[2/3] Сжатие файла дампа ({totalBakBytes / 1024.0 / 1024.0:F1} МБ)...");

    using (var zipStream = new FileStream(tempZipPath, FileMode.Create))
    using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
    {
        var entry = zip.CreateEntry(bakFileInfo.Name, CompressionLevel.Optimal);
        using var sourceStream = File.OpenRead(tempBakPath);
        using var entryStream = entry.Open();

        byte[] buffer = new byte[81920];
        long compressedBytesRead = 0;
        int bytesRead;

        while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            entryStream.Write(buffer, 0, bytesRead);
            compressedBytesRead += bytesRead;

            if (isManualRun)
            {
                DrawProgressBar("Сжатие", compressedBytesRead, totalBakBytes);
            }
        }
    }
    if (isManualRun) Console.WriteLine();
    if (File.Exists(tempBakPath)) File.Delete(tempBakPath);

    // =======================================================
    // ШАГ 3: ОТПРАВКА НА FTP С ПРОГРЕСС-БАРОМ
    // =======================================================
    var zipFileInfo = new FileInfo(tempZipPath);
    long totalZipBytes = zipFileInfo.Length;

    Console.WriteLine($"[3/3] Подключение к FTP ({ftpHost}) и передача архива ({totalZipBytes / 1024.0 / 1024.0:F1} МБ)...");

    using (var ftp = new FtpClient(ftpHost, ftpUser, ftpPass))
    {
        ftp.Encoding = Encoding.GetEncoding("windows-1251");
        ftp.Connect();

        string remoteDir = $"/{ftpRootFolder}/{cityName}/{officeName}";
        ftp.CreateDirectory(remoteDir);

        Action<FtpProgress> progress = p =>
        {
            if (isManualRun)
            {
                DrawProgressBar("Отправка FTP", (long)p.TransferredBytes, totalZipBytes);
            }
        };

        ftp.UploadFile(tempZipPath, $"{remoteDir}/{archiveFileName}", FtpRemoteExists.Overwrite, true, FtpVerify.None, progress);
        ftp.Disconnect();
    }
    if (isManualRun) Console.WriteLine();
    if (File.Exists(tempZipPath)) File.Delete(tempZipPath);

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"\n[УСПЕШНО] Архив доставлен: {cityName}/{officeName}/{archiveFileName}");
    Console.ResetColor();

    if (isManualRun)
    {
        Console.WriteLine("\nНажмите любую клавишу для закрытия окна...");
        Console.ReadKey();
    }
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\n[ОШИБКА] {ex.Message}");
    Console.ResetColor();

    try
    {
        EventLog.WriteEntry("Application", $"BackupAgent Error: {ex.Message}", EventLogEntryType.Error);
    }
    catch { /* Пропускаем, если нет прав на запись в системный EventLog */ }

    if (isManualRun)
    {
        Console.WriteLine("\nНажмите любую клавишу для выхода...");
        Console.ReadKey();
    }
}

// =======================================================
// ВАЛИДАЦИЯ И ВСПОМОГАТЕЛЬНЫЕ ФУНКЦИИ
// =======================================================
static void DrawProgressBar(string label, long current, long total, int barSize = 25)
{
    if (total == 0) return;

    double percentage = (double)current / total;
    if (percentage > 1.0) percentage = 1.0;

    int progressBlocks = (int)(percentage * barSize);
    string bar = new string('█', progressBlocks) + new string('░', barSize - progressBlocks);

    double currentMb = current / 1024.0 / 1024.0;
    double totalMb = total / 1024.0 / 1024.0;

    Console.Write($"\r[Agent] {label}: [{bar}] {percentage:P0} ({currentMb:F1} MB / {totalMb:F1} MB)");
}

static string ReadRequiredString(string prompt)
{
    while (true)
    {
        Console.Write(prompt);
        string input = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(input)) return input;

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Ошибка: поле не может быть пустым!");
        Console.ResetColor();
    }
}

static string ReadPasswordSecurely()
{
    var pass = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace && pass.Length > 0)
        {
            pass.Length--;
            Console.Write("\b \b");
        }
        else if (!char.IsControl(key.KeyChar))
        {
            pass.Append(key.KeyChar);
            Console.Write("*");
        }
    }
    Console.WriteLine();
    return pass.ToString();
}

static string EncryptSecret(string secret)
{
    if (string.IsNullOrEmpty(secret)) return "";
    byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.LocalMachine);
    return Convert.ToBase64String(encrypted);
}

static string DecryptSecret(string encryptedBase64)
{
    if (string.IsNullOrEmpty(encryptedBase64)) return "";
    byte[] decrypted = ProtectedData.Unprotect(Convert.FromBase64String(encryptedBase64), null, DataProtectionScope.LocalMachine);
    return Encoding.UTF8.GetString(decrypted);
}

static string FindPgDumpPath()
{
    string baseDir = @"C:\Program Files\PostgreSQL";
    if (Directory.Exists(baseDir))
    {
        var dirs = Directory.GetDirectories(baseDir).OrderByDescending(d => d);
        foreach (var dir in dirs)
        {
            string path = Path.Combine(dir, "bin", "pg_dump.exe");
            if (File.Exists(path)) return path;
        }
    }
    return "";
}
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

// Подтягиваем WinAPI функции для возможности скрытия окна
[DllImport("kernel32.dll")]
static extern IntPtr GetConsoleWindow();

[DllImport("user32.dll")]
static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

const int SW_HIDE = 0;

var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

bool hideConsole = bool.TryParse(configuration["AgentSettings:HideConsoleWindow"], out var hide) && hide;

// Если в конфиге включено скрытие окна — прячем консоль сразу при старте
if (hideConsole)
{
    var handle = GetConsoleWindow();
    ShowWindow(handle, SW_HIDE);
}

string cityName = configuration["AgentSettings:CityName"] ?? "UnknownCity";
string officeName = configuration["AgentSettings:OfficeName"] ?? "UnknownOffice";
string pointCode = configuration["AgentSettings:PointCode"] ?? "OP1";
string connectionString = configuration["AgentSettings:ConnectionString"] ?? @"Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True;";
string databaseName = configuration["AgentSettings:DatabaseName"] ?? "Exchange";
string targetRootPath = configuration["AgentSettings:TargetPath"] ?? @"C:\FTP\A8pro\Backups_V2";

string tempBackupFolder = @"C:\BackupTemp";
Directory.CreateDirectory(tempBackupFolder);

// Отрисовываем крупный баннер предупреждения, если консоль видна
if (!hideConsole)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("=======================================================================");
    Console.WriteLine("  [ВНИМАНИЕ!] ИДЁТ АВТОМАТИЧЕСКОЕ РЕЗЕРВНОЕ КОПИРОВАНИЕ БАЗЫ 1С");
    Console.WriteLine("  ПОЖАЛУЙСТА, НЕ ЗАКРЫВАЙТЕ ЭТО ОКНО ДО ОКОНЧАНИЯ ПРОЦЕССА!");
    Console.WriteLine("=======================================================================");
    Console.ResetColor();
    Console.WriteLine();
}

Console.WriteLine($"[Agent] Запуск снятия бэкапа MSSQL для {cityName}/{officeName}/{pointCode}...");
Console.WriteLine($"[Agent] Целевая база данных: {databaseName}\n");

string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");

string tempBakPath = Path.Combine(tempBackupFolder, $"{databaseName}_{timestamp}.bak");
string archiveFileName = $"{officeName}_{pointCode}_{timestamp}.zip";
string tempZipPath = Path.Combine(tempBackupFolder, archiveFileName);

string destinationDirectory = Path.Combine(targetRootPath, cityName, officeName);
Directory.CreateDirectory(destinationDirectory);
string destinationZipPath = Path.Combine(destinationDirectory, archiveFileName);

try
{
    // ==========================================
    // ШАГ 1: Выполнение BACKUP DATABASE в MSSQL
    // ==========================================
    Console.WriteLine("[1/3] Запрос к MSSQL: дамп базы данных...");

    using (var connection = new SqlConnection(connectionString))
    {
        connection.Open();

        string sqlQuery = $@"BACKUP DATABASE [{databaseName}] TO DISK = N'{tempBakPath}' WITH FORMAT, INIT, STATS = 10;";

        using (var command = new SqlCommand(sqlQuery, connection))
        {
            command.CommandTimeout = 3600;
            command.ExecuteNonQuery();
        }
    }

    Console.WriteLine($"[Success] Файл .bak сформирован по адресу: {tempBakPath}\n");

    // ==========================================
    // ШАГ 2: Локальное сжатие (.bak -> .zip)
    // ==========================================
    var bakFileInfo = new FileInfo(tempBakPath);
    long totalBakBytes = bakFileInfo.Length;

    Console.WriteLine($"[2/3] Сжатие файла дампа ({totalBakBytes / 1024.0 / 1024.0:F1} МБ)...");

    using (var zipStream = new FileStream(tempZipPath, FileMode.Create))
    {
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

                if (!hideConsole)
                {
                    DrawProgressBar("Сжатие ", compressedBytesRead, totalBakBytes);
                }
            }
        }
    }

    if (!hideConsole) Console.WriteLine("\n[Success] Сжатие завершено!\n");

    if (File.Exists(tempBakPath)) File.Delete(tempBakPath);

    // ==========================================
    // ШАГ 3: Передача (.zip в целевую папку)
    // ==========================================
    var zipFileInfo = new FileInfo(tempZipPath);
    long totalZipBytes = zipFileInfo.Length;

    Console.WriteLine($"[3/3] Передача архива ({totalZipBytes / 1024.0 / 1024.0:F1} МБ)...");

    using (var sourceStream = File.OpenRead(tempZipPath))
    using (var destinationStream = File.Create(destinationZipPath))
    {
        byte[] buffer = new byte[81920];
        long copiedBytes = 0;
        int bytesRead;

        while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            destinationStream.Write(buffer, 0, bytesRead);
            copiedBytes += bytesRead;

            if (!hideConsole)
            {
                DrawProgressBar("Отправка", copiedBytes, totalZipBytes);
            }
        }
    }

    if (!hideConsole) Console.WriteLine("\n[Success] Передача завершена!\n");

    if (File.Exists(tempZipPath)) File.Delete(tempZipPath);

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"\n[COMPLETE] Бэкап успешно доставлен: {destinationZipPath}");
    Console.ResetColor();

    if (!hideConsole)
    {
        Console.WriteLine("\nОкно закроется автоматически через 3 секунды...");
        Thread.Sleep(3000);
    }
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\n[Error] Ошибка выполнения: {ex.Message}");
    Console.ResetColor();

    if (!hideConsole)
    {
        Console.WriteLine("\nНажмите любую клавишу для выхода...");
        Console.ReadKey();
    }
}

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
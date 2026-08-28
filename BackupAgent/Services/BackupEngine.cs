using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using BackupAgent.Helpers;
using FluentFTP;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace BackupAgent.Services;

public static class BackupEngine
{
    public static void Run(string exeDir, bool isManualRun)
    {
        if (isManualRun)
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
            string ftpPass = SecurityService.DecryptSecret(configuration["AgentSettings:FtpPasswordEncrypted"]);
            string ftpHost = configuration["AgentSettings:FtpHost"] ?? "ftp.a8pro.kz";
            string ftpUser = configuration["AgentSettings:FtpUser"] ?? "A8pro";
            string ftpRootFolder = configuration["AgentSettings:FtpRootFolder"] ?? "Backups_V2";

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            string tempFolder = @"C:\BackupTemp";
            Directory.CreateDirectory(tempFolder);

            string tempBakPath = Path.Combine(tempFolder, $"{dbName}_{timestamp}.bak");
            string dbPrefix = dbType.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase) ? "PG" : "SQL";
            string archiveFileName = $"{officeName}_{pointCode}_{dbPrefix}_{timestamp}.zip";
            string tempZipPath = Path.Combine(tempFolder, archiveFileName);

            Console.WriteLine($"\n[1/3] Создание дампа базы ({dbType})...");

            if (dbType.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
            {
                string pgPass = SecurityService.DecryptSecret(configuration["AgentSettings:PgPasswordEncrypted"]);
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

            // ШАГ 2: СЖАТИЕ В ZIP
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
                        ConsoleHelper.DrawProgressBar("Сжатие", compressedBytesRead, totalBakBytes);
                    }
                }
            }
            if (isManualRun) Console.WriteLine();
            if (File.Exists(tempBakPath)) File.Delete(tempBakPath);

            // ШАГ 3: ОТПРАВКА НА FTP С RETRY POLICY
            var zipFileInfo = new FileInfo(tempZipPath);
            long totalZipBytes = zipFileInfo.Length;

            Console.WriteLine($"[3/3] Подключение к FTP ({ftpHost}) и передача архива ({totalZipBytes / 1024.0 / 1024.0:F1} МБ)...");

            RetryHelper.ExecuteWithRetry(() =>
            {
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
                            ConsoleHelper.DrawProgressBar("Отправка FTP", (long)p.TransferredBytes, totalZipBytes);
                        }
                    };

                    ftp.UploadFile(tempZipPath, $"{remoteDir}/{archiveFileName}", FtpRemoteExists.Overwrite, true, FtpVerify.None, progress);
                    ftp.Disconnect();
                }
            }, stepName: "передаче файла по FTP", maxRetries: 3, delaySeconds: 30);

            if (isManualRun) Console.WriteLine();
            if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
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
            catch { /* Пропускаем при отсутствии прав на запись в EventLog */ }

            if (isManualRun)
            {
                Console.WriteLine("\nНажмите любую клавишу для выхода...");
                Console.ReadKey();
            }
        }
    }
}
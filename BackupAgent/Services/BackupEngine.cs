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
        else
        {
            // Случайная задержка от 0 до 30 минут, чтобы размазать отправку по времени
            var random = new Random();
            int jitterSeconds = random.Next(0, 30 * 60);
            Thread.Sleep(jitterSeconds * 1000);
        }

        string tempBakPath = string.Empty;
        string tempZipPath = string.Empty;

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

            // 0. Предварительная очистка мусора от бывших аварийных сбоев
            CleanStaleTempFiles(tempFolder);

            tempBakPath = Path.Combine(tempFolder, $"{dbName}_{timestamp}.bak");
            string dbPrefix = dbType.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase) ? "PG" : "SQL";
            string archiveFileName = $"{officeName}_{pointCode}_{dbPrefix}_{timestamp}.zip";
            tempZipPath = Path.Combine(tempFolder, archiveFileName);

            try
            {
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
                    // 1. Получаем настройки и расшифровываем пароль
                    string sqlServer = configuration["AgentSettings:SqlServer"] ?? @"localhost\SQLEXPRESS";
                    string sqlUser = configuration["AgentSettings:SqlUser"] ?? "sa";
                    string sqlPassEncrypted = configuration["AgentSettings:SqlPasswordEncrypted"];
                    string sqlPass = SecurityService.DecryptSecret(sqlPassEncrypted);

                    var builder = new SqlConnectionStringBuilder
                    {
                        DataSource = sqlServer,
                        InitialCatalog = dbName,
                        UserID = sqlUser,
                        Password = sqlPass,
                        TrustServerCertificate = true,
                        ConnectTimeout = 30
                    };

                    using var connection = new SqlConnection(builder.ConnectionString);
                    connection.Open();

                    // 🛡2. Безопасное экранирование имени базы данных
                    string safeDbName = $"[{dbName.Replace("]", "]]")}]";

                    string sqlQuery = $@"BACKUP DATABASE {safeDbName} TO DISK = N'{tempBakPath}' WITH FORMAT, INIT;";

                    using var command = new SqlCommand(sqlQuery, connection);
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
            }
            finally
            {
                // Исходный файл .bak удаляем сразу после архивации, независимо от того, удачно ли прошло сжатие
                SafeDeleteFile(tempBakPath);
            }

            // ШАГ 3: ОТПРАВКА НА FTP С RETRY POLICY И АТОМАРНЫМ ПЕРЕИМЕНОВАНИЕМ
            var zipFileInfo = new FileInfo(tempZipPath);
            long totalZipBytes = zipFileInfo.Length;

            Console.WriteLine($"[3/3] Подключение к FTP ({ftpHost}) и передача архива ({totalZipBytes / 1024.0 / 1024.0:F1} МБ)...");

            RetryHelper.ExecuteWithRetry(() =>
            {
                using (var ftp = new FtpClient(ftpHost, ftpUser, ftpPass))
                {
                    ftp.Encoding = Encoding.GetEncoding("windows-1251");

                    // ⚙️ Настройка жестких таймаутов, чтобы не застревать при обрывах сети
                    ftp.Config.ConnectTimeout = 15000;          // 15 сек на подключение
                    ftp.Config.ReadTimeout = 20000;             // 20 сек на чтение
                    ftp.Config.DataConnectionConnectTimeout = 15000;
                    ftp.Config.DataConnectionReadTimeout = 20000;

                    ftp.Connect();

                    string remoteDir = $"/{ftpRootFolder}/{cityName}/{officeName}";
                    ftp.CreateDirectory(remoteDir);

                    string remoteTmpPath = $"{remoteDir}/{archiveFileName}.tmp";
                    string remoteFinalPath = $"{remoteDir}/{archiveFileName}";

                    Action<FtpProgress> progress = p =>
                    {
                        if (isManualRun)
                        {
                            ConsoleHelper.DrawProgressBar("Отправка FTP", (long)p.TransferredBytes, totalZipBytes);
                        }
                    };

                    // 🧹 Если от прошлого сорванного соединения на FTP остался недогруженный .tmp — счищаем его
                    if (ftp.FileExists(remoteTmpPath))
                    {
                        try { ftp.DeleteFile(remoteTmpPath); } catch { }
                    }

                    // 1. Загружаем во временный файл .tmp
                    ftp.UploadFile(tempZipPath, remoteTmpPath, FtpRemoteExists.Overwrite, true, FtpVerify.None, progress);

                    // 2. Сверяем точный размер файла на FTP с локальным
                    long remoteSize = ftp.GetFileSize(remoteTmpPath);
                    if (remoteSize != totalZipBytes)
                    {
                        try { ftp.DeleteFile(remoteTmpPath); } catch { }
                        throw new Exception($"Размер файла на FTP ({remoteSize} Б) не совпадает с локальным ({totalZipBytes} Б). Передача прервана.");
                    }

                    // 3. Атомарно переименовываем .tmp -> .zip
                    if (ftp.FileExists(remoteFinalPath))
                    {
                        try { ftp.DeleteFile(remoteFinalPath); } catch { }
                    }
                    ftp.Rename(remoteTmpPath, remoteFinalPath);

                    ftp.Disconnect();
                }
            }, stepName: "передаче файла по FTP", maxRetries: 3, initialDelaySeconds: 30);

            if (isManualRun) Console.WriteLine();
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
        finally
        {
            // Архив .zip удаляем всегда по окончании работы, даже если упала сеть или была ошибка FTP
            SafeDeleteFile(tempZipPath);
        }
    }

    private static void CleanStaleTempFiles(string tempFolder)
    {
        try
        {
            if (!Directory.Exists(tempFolder)) return;

            var tempDir = new DirectoryInfo(tempFolder);

                        // При старте очищаем ЛЮБЫЕ сторонние файлы и старые архивы из Temp
            foreach (var file in tempDir.GetFiles())
            {
                SafeDeleteFile(file.FullName);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warning] Ошибка при очистке старых файлов в Temp: {ex.Message}");
        }
    }

    private static void SafeDeleteFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return;

        // Небольшая задержка, чтобы Защитник Windows или ОС успели снять дескриптор с файла
        Thread.Sleep(500);

        for (int i = 0; i < 3; i++)// Пытаемся удалить файл до 3 раз
        {
            try
            {
                File.Delete(filePath);
                break;
            }
            catch (IOException)
            {
                // Если файл заблокирован, ждем 1 секунду и пробуем снова
                Thread.Sleep(1000);
            }
            catch (UnauthorizedAccessException)// Если файл заблокирован, ждем 1 секунду и пробуем снова
            {
                try
                {
                    File.SetAttributes(filePath, FileAttributes.Normal);
                    File.Delete(filePath);
                    break;
                }
                catch { }
            }
            catch
            {
                break;
            }
        }
    }
}
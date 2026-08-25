using BackupServer.Infrastructure.Services;

namespace BackupServer.Api.BackgroundServices;

public class FileScannerWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<FileScannerWorker> _logger;
    private readonly string _scanPath;

    public FileScannerWorker(
        IServiceProvider serviceProvider,
        ILogger<FileScannerWorker> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _scanPath = configuration["BackupSettings:ScanPath"] ?? @"C:\FTP\A8pro\Backups_V2";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Фоновый сканер бэкапов запущен. Путь: {ScanPath}", _scanPath);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var scanner = scope.ServiceProvider.GetRequiredService<FileScannerService>();

                await scanner.ScanDirectoryAsync(_scanPath, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при сканировании папки бэкапов.");
            }

            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }
}
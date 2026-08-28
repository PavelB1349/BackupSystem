namespace BackupAgent.Helpers;

public static class RetryHelper
{
    public static void ExecuteWithRetry(Action action, string stepName, int maxRetries = 3, int delaySeconds = 10)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex)
            {
                if (attempt == maxRetries)
                {
                    throw new Exception($"[После {maxRetries} попыток] {ex.Message}", ex);
                }

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n[ВНИМАНИЕ] Сбой при {stepName} (Попытка {attempt}/{maxRetries}): {ex.Message}");
                Console.WriteLine($"[RETRY] Повторная попытка через {delaySeconds} сек...");
                Console.ResetColor();

                Thread.Sleep(delaySeconds * 1000);
            }
        }
    }
}
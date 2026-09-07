namespace BackupAgent.Helpers;

public static class RetryHelper
{
    // maxRetries = 4 попытки, initialDelaySeconds = 30 секунд
    public static void ExecuteWithRetry(Action action, string stepName, int maxRetries = 4, int initialDelaySeconds = 30)
    {
        var random = new Random();

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                action();
                return; // Если отправка прошла успешно — выходим
            }
            catch (Exception ex)
            {
                if (attempt == maxRetries)
                {
                    throw new Exception($"[После {maxRetries} попыток] {ex.Message}", ex);
                }

                // ⚡ РАСЧЕТ ПАУЗЫ:
                // Попытка 1: 30 сек + random (1..5) = ~32 сек
                // Попытка 2: 60 сек + random (1..5) = ~63 сек (1 минута)
                // Попытка 3: 120 сек + random (1..5) = ~124 сек (2 минуты)
                int delaySeconds = initialDelaySeconds * (int)Math.Pow(2, attempt - 1) + random.Next(1, 6);

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n[ВНИМАНИЕ] Сбой при {stepName} (Попытка {attempt}/{maxRetries}): {ex.Message}");
                Console.WriteLine($"[RETRY] Следующая попытка через {delaySeconds} сек...");
                Console.ResetColor();

                Thread.Sleep(TimeSpan.FromSeconds(delaySeconds));
            }
        }
    }
}
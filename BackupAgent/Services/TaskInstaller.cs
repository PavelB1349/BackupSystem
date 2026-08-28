using System.Diagnostics;

namespace BackupAgent.Services;

public static class TaskInstaller
{
    public static void Run(string exePath)
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
    }
}
using System.Text;

namespace BackupAgent.Helpers;

public static class ConsoleHelper
{
    public static void DrawProgressBar(string label, long current, long total, int barSize = 25)
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

    public static string ReadRequiredString(string prompt)
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

    public static string ReadPasswordSecurely()
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

    public static string FindPgDumpPath()
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
}
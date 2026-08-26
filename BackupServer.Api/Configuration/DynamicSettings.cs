namespace BackupServer.Api.Configuration
{
    public static class DynamicSettings
    {
        public static int OverdueDays { get; set; } = 1;
        public static int ClosedDays { get; set; } = 30;
        public static int MaxBackupsPerPoint { get; set; } = 3;

        public static void Init(IConfiguration config)
        {
            OverdueDays = config.GetValue<int>("MonitoringSettings:OverdueDays", 1);
            ClosedDays = config.GetValue<int>("MonitoringSettings:ClosedDays", 30);
            MaxBackupsPerPoint = config.GetValue<int>("MonitoringSettings:MaxBackupsPerPoint", 3);
        }
    }
}

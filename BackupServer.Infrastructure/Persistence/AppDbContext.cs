using BackupServer.Core.Entities;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Text;

namespace BackupServer.Infrastructure.Persistence
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<City> Cities => Set<City>();
        public DbSet<ExchangeOffice> ExchangeOffices => Set<ExchangeOffice>();
        public DbSet<Point> Points => Set<Point>();
        public DbSet<BackupLog> BackupLogs => Set<BackupLog>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Индексы для быстрого поиска по коду кассы и дате лога
            modelBuilder.Entity<Point>()
                .HasIndex(p => p.Code);

            modelBuilder.Entity<BackupLog>()
                .HasIndex(b => b.ProcessedAt);
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BackupServer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDatabaseTypeToPoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DbType",
                table: "Points",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DbType",
                table: "Points");
        }
    }
}

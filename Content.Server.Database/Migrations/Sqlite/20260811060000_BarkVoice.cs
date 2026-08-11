using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    public partial class BarkVoice : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bark_voice",
                table: "profile",
                type: "TEXT",
                nullable: false,
                defaultValue: "Bark_human_1");

            migrationBuilder.AddColumn<float>(
                name: "bark_pitch",
                table: "profile",
                type: "REAL",
                nullable: false,
                defaultValue: 0f);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bark_voice",
                table: "profile");

            migrationBuilder.DropColumn(
                name: "bark_pitch",
                table: "profile");
        }
    }
}

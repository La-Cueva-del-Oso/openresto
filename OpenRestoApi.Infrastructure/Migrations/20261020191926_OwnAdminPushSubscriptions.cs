using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenRestoApi.Migrations
{
    /// <inheritdoc />
    public partial class OwnAdminPushSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing rows have no owner and cannot be attributed to one, so they go: a
            // subscription nobody can be held to would keep delivering bookings to a browser whose
            // user may since have been deactivated. Each admin re-enables notifications. Doing this
            // first also keeps the NOT NULL + FK below satisfiable (a default of 0 names no
            // account); a fresh install has no rows, so both paths end on the same schema.
            migrationBuilder.Sql("DELETE FROM AdminPushSubscriptions;");

            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "AdminPushSubscriptions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_AdminPushSubscriptions_UserId",
                table: "AdminPushSubscriptions",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_AdminPushSubscriptions_AdminCredentials_UserId",
                table: "AdminPushSubscriptions",
                column: "UserId",
                principalTable: "AdminCredentials",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AdminPushSubscriptions_AdminCredentials_UserId",
                table: "AdminPushSubscriptions");

            migrationBuilder.DropIndex(
                name: "IX_AdminPushSubscriptions_UserId",
                table: "AdminPushSubscriptions");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "AdminPushSubscriptions");
        }
    }
}

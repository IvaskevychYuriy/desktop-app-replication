using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Replication.Tests.Integration.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Shelf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", nullable: true),
                    SyncBase = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    SyncMask = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    SyncOrigin = table.Column<string>(type: "TEXT", nullable: true),
                    SyncVersion = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shelf", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Product",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Price = table.Column<long>(type: "INTEGER", nullable: false),
                    ShelfId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", nullable: true),
                    SyncBase = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    SyncMask = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    SyncOrigin = table.Column<string>(type: "TEXT", nullable: true),
                    SyncVersion = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Product", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Product_Shelf_ShelfId",
                        column: x => x.ShelfId,
                        principalTable: "Shelf",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Product_InstanceId",
                table: "Product",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_Product_ShelfId",
                table: "Product",
                column: "ShelfId");

            migrationBuilder.CreateIndex(
                name: "IX_Product_SyncVersion",
                table: "Product",
                column: "SyncVersion");

            migrationBuilder.CreateIndex(
                name: "IX_Shelf_InstanceId",
                table: "Shelf",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_Shelf_SyncVersion",
                table: "Shelf",
                column: "SyncVersion");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Product");

            migrationBuilder.DropTable(
                name: "Shelf");
        }
    }
}

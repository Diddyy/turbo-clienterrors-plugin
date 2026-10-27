using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TurboClientErrors.Database.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase().Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder
                .CreateTable(
                    name: "clienterrors_facets",
                    columns: table => new
                    {
                        id = table
                            .Column<int>(type: "int", nullable: false)
                            .Annotation(
                                "MySql:ValueGenerationStrategy",
                                MySqlValueGenerationStrategy.IdentityColumn
                            ),
                        issue_id = table.Column<int>(type: "int", nullable: false),
                        facet = table
                            .Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        value = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        occurrences = table.Column<int>(type: "int", nullable: false),
                        first_seen = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                        last_seen = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_clienterrors_facets", x => x.id);
                    }
                )
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder
                .CreateTable(
                    name: "clienterrors_fingerprints",
                    columns: table => new
                    {
                        id = table
                            .Column<int>(type: "int", nullable: false)
                            .Annotation(
                                "MySql:ValueGenerationStrategy",
                                MySqlValueGenerationStrategy.IdentityColumn
                            ),
                        fingerprint = table
                            .Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        issue_id = table.Column<int>(type: "int", nullable: false),
                        build = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_clienterrors_fingerprints", x => x.id);
                    }
                )
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder
                .CreateTable(
                    name: "clienterrors_issue_days",
                    columns: table => new
                    {
                        id = table
                            .Column<int>(type: "int", nullable: false)
                            .Annotation(
                                "MySql:ValueGenerationStrategy",
                                MySqlValueGenerationStrategy.IdentityColumn
                            ),
                        issue_id = table.Column<int>(type: "int", nullable: false),
                        day = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                        occurrences = table.Column<int>(type: "int", nullable: false),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_clienterrors_issue_days", x => x.id);
                    }
                )
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder
                .CreateTable(
                    name: "clienterrors_issues",
                    columns: table => new
                    {
                        id = table
                            .Column<int>(type: "int", nullable: false)
                            .Annotation(
                                "MySql:ValueGenerationStrategy",
                                MySqlValueGenerationStrategy.IdentityColumn
                            ),
                        signature = table
                            .Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        kind = table
                            .Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        message = table
                            .Column<string>(type: "varchar(512)", maxLength: 512, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        top_frame = table
                            .Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        status = table
                            .Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        occurrences = table.Column<int>(type: "int", nullable: false),
                        players = table.Column<int>(type: "int", nullable: false),
                        first_seen = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                        last_seen = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                        last_build = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        resolved_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                        resolved_by = table.Column<int>(type: "int", nullable: true),
                        reopened_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                        reopened_build = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        report = table
                            .Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        note = table
                            .Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_clienterrors_issues", x => x.id);
                    }
                )
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder
                .CreateTable(
                    name: "clienterrors_occurrences",
                    columns: table => new
                    {
                        id = table
                            .Column<int>(type: "int", nullable: false)
                            .Annotation(
                                "MySql:ValueGenerationStrategy",
                                MySqlValueGenerationStrategy.IdentityColumn
                            ),
                        issue_id = table.Column<int>(type: "int", nullable: false),
                        occurred_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                        player_id = table.Column<int>(type: "int", nullable: false),
                        kind = table
                            .Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        message = table
                            .Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        stack = table
                            .Column<string>(type: "mediumtext", maxLength: 512, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        component_stack = table
                            .Column<string>(type: "text", maxLength: 512, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        source = table
                            .Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        breadcrumbs = table
                            .Column<string>(type: "text", maxLength: 512, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        build = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        production = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        browser = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        platform = table
                            .Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        user_agent = table
                            .Column<string>(type: "varchar(512)", maxLength: 512, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        page = table
                            .Column<string>(type: "varchar(256)", maxLength: 256, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        room_id = table.Column<int>(type: "int", nullable: true),
                        viewport = table
                            .Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        renderer = table
                            .Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                            .Annotation("MySql:CharSet", "utf8mb4"),
                        uptime_ms = table.Column<long>(type: "bigint", nullable: false),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_clienterrors_occurrences", x => x.id);
                    }
                )
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_clienterrors_facets_issue_id_facet_value",
                table: "clienterrors_facets",
                columns: new[] { "issue_id", "facet", "value" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_clienterrors_fingerprints_fingerprint",
                table: "clienterrors_fingerprints",
                column: "fingerprint",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_clienterrors_fingerprints_issue_id",
                table: "clienterrors_fingerprints",
                column: "issue_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_clienterrors_issue_days_day",
                table: "clienterrors_issue_days",
                column: "day"
            );

            migrationBuilder.CreateIndex(
                name: "IX_clienterrors_issue_days_issue_id_day",
                table: "clienterrors_issue_days",
                columns: new[] { "issue_id", "day" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_clienterrors_issues_last_seen",
                table: "clienterrors_issues",
                column: "last_seen"
            );

            migrationBuilder.CreateIndex(
                name: "IX_clienterrors_issues_signature",
                table: "clienterrors_issues",
                column: "signature"
            );

            migrationBuilder.CreateIndex(
                name: "IX_clienterrors_issues_status_last_seen",
                table: "clienterrors_issues",
                columns: new[] { "status", "last_seen" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_clienterrors_occurrences_issue_id_id",
                table: "clienterrors_occurrences",
                columns: new[] { "issue_id", "id" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_clienterrors_occurrences_occurred_at",
                table: "clienterrors_occurrences",
                column: "occurred_at"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "clienterrors_facets");

            migrationBuilder.DropTable(name: "clienterrors_fingerprints");

            migrationBuilder.DropTable(name: "clienterrors_issue_days");

            migrationBuilder.DropTable(name: "clienterrors_issues");

            migrationBuilder.DropTable(name: "clienterrors_occurrences");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeveMobileLPR.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trips",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    started_at = table.Column<string>(type: "TEXT", nullable: false),
                    ended_at = table.Column<string>(type: "TEXT", nullable: true),
                    distance_meters = table.Column<double>(type: "REAL", nullable: false),
                    start_latitude = table.Column<double>(type: "REAL", nullable: true),
                    start_longitude = table.Column<double>(type: "REAL", nullable: true),
                    start_accuracy_meters = table.Column<float>(type: "REAL", nullable: true),
                    end_latitude = table.Column<double>(type: "REAL", nullable: true),
                    end_longitude = table.Column<double>(type: "REAL", nullable: true),
                    end_accuracy_meters = table.Column<float>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trips", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sightings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    normalized_plate = table.Column<string>(type: "TEXT", nullable: false),
                    display_plate = table.Column<string>(type: "TEXT", nullable: false),
                    region = table.Column<string>(type: "TEXT", nullable: true),
                    first_seen_at = table.Column<string>(type: "TEXT", nullable: false),
                    last_seen_at = table.Column<string>(type: "TEXT", nullable: false),
                    confidence = table.Column<float>(type: "REAL", nullable: false),
                    observation_count = table.Column<int>(type: "INTEGER", nullable: false),
                    latitude = table.Column<double>(type: "REAL", nullable: true),
                    longitude = table.Column<double>(type: "REAL", nullable: true),
                    location_accuracy_meters = table.Column<float>(type: "REAL", nullable: true),
                    make = table.Column<string>(type: "TEXT", nullable: true),
                    model = table.Column<string>(type: "TEXT", nullable: true),
                    catalog_price = table.Column<decimal>(type: "NUMERIC", nullable: true),
                    registration_year = table.Column<int>(type: "INTEGER", nullable: true),
                    fuel_description = table.Column<string>(type: "TEXT", nullable: true),
                    body_type = table.Column<string>(type: "TEXT", nullable: true),
                    trip_id = table.Column<long>(type: "INTEGER", nullable: true),
                    snapshot_reference = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sightings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sightings_trips_trip_id",
                        column: x => x.trip_id,
                        principalTable: "trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "trip_points",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    trip_id = table.Column<long>(type: "INTEGER", nullable: false),
                    recorded_at = table.Column<string>(type: "TEXT", nullable: false),
                    latitude = table.Column<double>(type: "REAL", nullable: false),
                    longitude = table.Column<double>(type: "REAL", nullable: false),
                    accuracy_meters = table.Column<float>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trip_points", x => x.Id);
                    table.ForeignKey(
                        name: "FK_trip_points_trips_trip_id",
                        column: x => x.trip_id,
                        principalTable: "trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sightings_last_seen",
                table: "sightings",
                column: "last_seen_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_sightings_plate_last_seen",
                table: "sightings",
                columns: new[] { "normalized_plate", "last_seen_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_sightings_price",
                table: "sightings",
                column: "catalog_price",
                descending: new bool[0],
                filter: "\"catalog_price\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sightings_trip_last_seen",
                table: "sightings",
                columns: new[] { "trip_id", "last_seen_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_trip_points_trip_time",
                table: "trip_points",
                columns: new[] { "trip_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_trips_started",
                table: "trips",
                column: "started_at",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sightings");

            migrationBuilder.DropTable(
                name: "trip_points");

            migrationBuilder.DropTable(
                name: "trips");
        }
    }
}

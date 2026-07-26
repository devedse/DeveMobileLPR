namespace DeveMobileLPR.RdwDownloader;

internal sealed record DatasetSnapshot(
    string Id,
    string Name,
    long RowsUpdatedAt,
    IReadOnlySet<string> Fields);

internal sealed record VehicleSourceRow(
    string CursorPlate,
    string? Make,
    string? Model,
    long? CatalogPrice,
    int? RegistrationYear,
    string? BodyType);

internal sealed record FuelSourceRow(
    string CursorPlate,
    string CursorSequence,
    string? Description);

internal sealed record ImportState(
    string DatasetId,
    long RowsUpdatedAt,
    string? LastPlate,
    string? LastSequence,
    long ImportedRows,
    bool Completed,
    long? SampleLimit);

internal sealed record ImportProgress(
    string DatasetName,
    long ImportedRows,
    long ExpectedRows,
    TimeSpan Elapsed,
    bool Resumed);

internal sealed record ImportResult(
    string OutputPath,
    long VehicleRows,
    long FuelRows,
    long VehiclesWithFuel,
    bool IsSample);

internal interface IRdwSource
{
    Task<DatasetSnapshot> GetSnapshotAsync(string datasetId, CancellationToken cancellationToken);

    Task<long> GetRowCountAsync(string datasetId, CancellationToken cancellationToken);

    Task<IReadOnlyList<VehicleSourceRow>> GetVehiclePageAsync(
        string? afterPlate,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FuelSourceRow>> GetFuelPageAsync(
        string? afterPlate,
        string? afterSequence,
        int limit,
        CancellationToken cancellationToken);
}

using System.Globalization;

namespace DeveMobileLPR.RdwDownloader;

internal static class ProgramEntry
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        RdwDownloaderOptions options;
        try
        {
            options = RdwDownloaderOptions.Parse(
                arguments,
                Directory.GetCurrentDirectory(),
                Environment.GetEnvironmentVariable(RdwDownloaderOptions.AppTokenEnvironmentVariable));
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(RdwDownloaderOptions.Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(RdwDownloaderOptions.Usage);
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            Console.WriteLine("DeveMobileLPR RDW database builder");
            Console.WriteLine($"Output: {options.OutputPath}");
            Console.WriteLine($"Page size: {options.PageSize:N0}");
            Console.WriteLine($"Socrata app token: {(options.AppToken is null ? "not configured (shared public quota)" : "configured")}");
            if (options.SampleRows is { } sampleRows)
            {
                Console.WriteLine($"SAMPLE MODE: at most {sampleRows:N0} rows from each dataset; do not use this database as a complete RDW copy.");
            }

            var buildPath = options.OutputPath + ".building";
            if (File.Exists(buildPath) && !options.Restart)
            {
                Console.WriteLine($"Resuming partial database: {buildPath}");
            }

            using var httpClient = SocrataRdwClient.CreateHttpClient();
            var source = new SocrataRdwClient(httpClient, options.AppToken, message => Console.Error.WriteLine(message));
            var service = new RdwImportService(source);
            var result = await service.RunAsync(
                options,
                new InlineProgress<ImportProgress>(WriteProgress),
                cancellation.Token).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"Created {result.OutputPath}");
            Console.WriteLine($"Vehicles: {result.VehicleRows:N0}");
            Console.WriteLine($"Fuel rows processed: {result.FuelRows:N0}");
            Console.WriteLine($"Vehicles enriched with fuel: {result.VehiclesWithFuel:N0}");
            Console.WriteLine(result.IsSample
                ? "Result is a bounded sample database."
                : "Result is a complete, validated RDW snapshot ready for Android import.");
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Cancelled. The resumable .building database was preserved.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"RDW database build failed: {exception.Message}");
            Console.Error.WriteLine("The previous final database was not changed. A valid partial .building database can be resumed.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static void WriteProgress(ImportProgress progress)
    {
        var percent = progress.ExpectedRows == 0
            ? 100d
            : Math.Min(100d, progress.ImportedRows * 100d / progress.ExpectedRows);
        var rowsPerSecond = progress.Elapsed.TotalSeconds <= 0
            ? 0d
            : progress.ImportedRows / progress.Elapsed.TotalSeconds;
        var eta = rowsPerSecond <= 0 || progress.ImportedRows >= progress.ExpectedRows
            ? string.Empty
            : $" · ETA {TimeSpan.FromSeconds((progress.ExpectedRows - progress.ImportedRows) / rowsPerSecond):hh\\:mm\\:ss}";
        Console.WriteLine(
            $"{progress.DatasetName,-8} {progress.ImportedRows,12:N0}/{progress.ExpectedRows:N0} " +
            $"({percent.ToString("F2", CultureInfo.InvariantCulture)}%) · {rowsPerSecond:N0} rows/s{eta}" +
            (progress.Resumed ? " · resumed" : string.Empty));
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}

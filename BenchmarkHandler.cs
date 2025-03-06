using System.Text.Json;
using plot_twist_back_end;

public class BenchmarkHandler
{
    private class BenchmarkEntry
    {
        public double TimeToProcessBrushLocally { get; set; }
        public double TimeToUpdatePlots { get; set; }
        public double Ping { get; set; }
        public double TimeToProcess { get; set; } // Only for SentBrushTimings
        public long Time { get; set; }
    }

    private class ClientBenchmark
    {
        public int ClientId { get; set; }
        public BenchmarkConfig Config { get; set; }
        public List<BenchmarkEntry> BenchMarkData { get; set; } = new();
    }

    private readonly Dictionary<int, ClientBenchmark> _sentBrushTimings = new();
    private readonly Dictionary<int, ClientBenchmark> _receivedBrushTimings = new();
    private BenchmarkConfig? _referenceConfig;

    public void AddClient(int clientId, BenchmarkConfig config)
    {
        var configToCompare = config;
        configToCompare.clientId = 0;
        configToCompare.dataSetNum = 0;

        if (_referenceConfig.HasValue)
        {
            var refConfig = _referenceConfig.Value;
            refConfig.clientId = 0;
            refConfig.dataSetNum = 0;

            if (!configToCompare.Equals(refConfig))
            {
                throw new InvalidOperationException("All clients must have the same BenchmarkConfig (except clientId and dataSetNum)." );
            }
        }
        else
        {
            _referenceConfig = config;
        }

        var clientBenchmark = new ClientBenchmark { ClientId = clientId, Config = config };
        _sentBrushTimings[clientId] = clientBenchmark;
        _receivedBrushTimings[clientId] = new ClientBenchmark { ClientId = clientId, Config = config };
    }

    public void StoreSentBrushTimings(int clientId, double timeToProcessBrushLocally, double timeToUpdatePlots, double ping, double timeToProcess)
    {
        _sentBrushTimings[clientId].BenchMarkData.Add(new BenchmarkEntry
        {
            TimeToProcessBrushLocally = timeToProcessBrushLocally,
            TimeToUpdatePlots = timeToUpdatePlots,
            Ping = ping,
            TimeToProcess = timeToProcess,
            Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    public void StoreReceivedBrushTimings(int clientId, double timeToProcessBrushLocally, double timeToUpdatePlots, double ping)
    {
        _receivedBrushTimings[clientId].BenchMarkData.Add(new BenchmarkEntry
        {
            TimeToProcessBrushLocally = timeToProcessBrushLocally,
            TimeToUpdatePlots = timeToUpdatePlots,
            Ping = ping,
            Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    public void DownloadData()
    {
        var config = _referenceConfig!.Value;
        string directoryPath = "benchMark";
        Directory.CreateDirectory(directoryPath); // Ensure the directory exists

        string fileName = Path.Combine(directoryPath, 
            $"benchmark_{config.typeOfData}_{config.plotsAmount}_{config.columnsAmount}_{config.catColumnsAmount}_{config.entriesAmount}_{config.dimensionsSelected}_{config.catDimensionsSelected}_{config.fieldGroupsAmount}_{config.brushSize}_{config.stepSize}_{config.numberOfClientBrushing}_{config.numberOfDataSets}_{config.testDuration}.json");

        var data = new
        {
            SentBrushTimingsPerClient = _sentBrushTimings.Values,
            ReceivedBrushTimingsPerClient = _receivedBrushTimings.Values
        };

        string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(fileName, json);
        Console.WriteLine($"Benchmark data saved to {fileName}");
    }
}

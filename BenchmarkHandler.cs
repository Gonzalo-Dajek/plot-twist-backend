using System.Text.Json;
using System.Text.Json.Serialization;
using plot_twist_back_end;

public class BenchmarkHandler
{
    private class BenchmarkEntry
    {
        public double? TimeToProcessBrushLocally { get; set; }
        public double? TimeToUpdatePlots { get; set; }
        public double? Ping { get; set; }
        public double? TimeToProcess { get; set; } // Only for SentBrushTimings
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
                throw new InvalidOperationException("All clients must have the same BenchmarkConfig (except clientId and dataSetNum).");
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

    public void StoreSentBrushTimings(int clientId, double? timeToProcessBrushLocally, double? timeToUpdatePlots, double? ping, double? timeToProcess)
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

    public void StoreReceivedBrushTimings(int clientId, double? timeToProcessBrushLocally, double? timeToUpdatePlots, double? ping)
    {
        _receivedBrushTimings[clientId].BenchMarkData.Add(new BenchmarkEntry
        {
            TimeToProcessBrushLocally = timeToProcessBrushLocally,
            TimeToUpdatePlots = timeToUpdatePlots,
            Ping = ping,
            Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    public void StorePing(int clientId, long pingMs, int isSent)
    {
        var isSentBool = isSent == 1;
    
        if ((isSentBool && _sentBrushTimings.ContainsKey(clientId)) || 
            (!isSentBool && _receivedBrushTimings.ContainsKey(clientId)))
        {
            var entry = new BenchmarkEntry
            {
                Ping = pingMs,
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            var targetList = isSentBool ? _sentBrushTimings[clientId].BenchMarkData : _receivedBrushTimings[clientId].BenchMarkData;
            targetList.Add(entry);
        }
    }


    public void DownloadData()
    {
        var config = _referenceConfig!.Value;
        string directoryPath = "benchMark";
        Directory.CreateDirectory(directoryPath);

        string fileName = Path.Combine(directoryPath,
            $"benchmark_dataType:{config.dataDistribution}_plotsAmt:{config.plotsAmount}_numColumnsAmt:{config.columnsAmount}_catColumnsAmt:{config.catColumnsAmount}_rows:{config.entriesAmount}_brushedNumDims:{config.dimensionsSelected}_catDimsSelected:{config.catDimensionsSelected}_numLinks:{config.fieldGroupsAmount}_brushSize:{config.brushSize}_brushSpeed:{config.stepSize}_numOfClientsBrushing:{config.numberOfClientBrushing}_dataSetsAmt:{config.numberOfDataSets}_testDuration:{config.testDuration}.json");

        var data = new
        {
            SentBrushTimingsPerClient = _sentBrushTimings.Values,
            ReceivedBrushTimingsPerClient = _receivedBrushTimings.Values
        };

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        string json = JsonSerializer.Serialize(data, jsonOptions);
        File.WriteAllText(fileName, json);
        Console.WriteLine($"Benchmark data saved to {fileName}");
    }

    public void Reset()
    {
        _sentBrushTimings.Clear();
        _receivedBrushTimings.Clear();
        _referenceConfig = null;
    }
}

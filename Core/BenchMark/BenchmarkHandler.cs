using System.Text.Json;
using System.Text.Json.Serialization;
using plot_twist_back_end;
using plot_twist_back_end.Messages;

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

    private Dictionary<int, ClientBenchmark> _sentBrushTimings = new();
    private Dictionary<int, ClientBenchmark> _receivedBrushTimings = new();
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
        if (!_sentBrushTimings.TryGetValue(clientId, out var clientBenchmark))
        {
            return; 
        }

        clientBenchmark.BenchMarkData.Add(new BenchmarkEntry
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
        if (!_receivedBrushTimings.TryGetValue(clientId, out var clientBenchmark))
        {
            return; 
        }

        clientBenchmark.BenchMarkData.Add(new BenchmarkEntry
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
            $"AVG_{config.dataDistribution}" +
            $"_plots:{config.plotsAmount}" +
            $"_cols:{config.numColumnsAmount}" +
            $"_catCols:{config.catColumnsAmount}" +
            $"_rows:{config.entriesAmount}" +
            $"_dims:{config.numDimensionsSelected}" +
            $"_catDims:{config.catDimensionsSelected}" +
            $"_links:{config.numFieldGroupsAmount}" +
            $"_catLinks:{config.catFieldGroupsAmount}" +
            $"_bSize:{config.brushSize.ToString("0.###")}" +
            $"_bSpeed:{config.stepSize.ToString("0.###")}" +
            $"_clients:{config.numberOfClientBrushing}" +
            $"_sets:{config.numberOfDataSets}" +
            $"_duration:{config.testDuration}" +
            $"_setNum:{config.dataSetNum}" +
            $"_client:{config.clientId}.json");

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
    
public void DownloadAveragedData()
{
    var config = _referenceConfig!.Value;
    string directoryPath = "BenchMarkResults";
    Directory.CreateDirectory(directoryPath);

    string fileName = Path.Combine(directoryPath,
        $"dataDist:{config.dataDistribution}" +
        $"_plots:{config.plotsAmount}" +
        $"_cols:{config.numColumnsAmount}" +
        $"_catCols:{config.catColumnsAmount}" +
        $"_rows:{config.entriesAmount}" +
        $"_dims:{config.numDimensionsSelected}" +
        $"_catDims:{config.catDimensionsSelected}" +
        $"_links:{config.numFieldGroupsAmount}" +
        $"_catLinks:{config.catFieldGroupsAmount}" +
        $"_bSize:{config.brushSize.ToString("0.###")}" +
        $"_bSpeed:{config.stepSize.ToString("0.###")}" +
        $"_clients:{config.numberOfClientBrushing}" +
        $"_sets:{config.numberOfDataSets}" +
        $"_duration:{config.testDuration}.json");
    


    var sentBrushData = ComputeAverages2(_sentBrushTimings, includeTimeToProcess: true);
    var receivedBrushData = ComputeAverages2(_receivedBrushTimings, includeTimeToProcess: false);
    
    var sentBrushData2 = ComputeAverages(_sentBrushTimings, includeTimeToProcess: true);
    var receivedBrushData2 = ComputeAverages(_receivedBrushTimings, includeTimeToProcess: false);

    var avgSentBrushTimings = ComputeOverallAverages(sentBrushData2);
    var avgReceivedBrushTimings = ComputeOverallAverages(receivedBrushData2);
    
    var summarizedData = new
    {
        SentBrushTimingsPerClient = sentBrushData,
        ReceivedBrushTimingsPerClient = receivedBrushData,
        AvgSentBrushTimings = avgSentBrushTimings,
        AvgReceivedBrushTimings = avgReceivedBrushTimings
    };

    var jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    string json = JsonSerializer.Serialize(summarizedData, jsonOptions);
    File.WriteAllText(fileName, json);
    Console.WriteLine($"Averaged benchmark data saved to {fileName}");
}

private static Dictionary<int, object> ComputeAverages2(Dictionary<int, ClientBenchmark> data, bool includeTimeToProcess)
{
    var result = new Dictionary<int, object>();

    foreach (var (clientId, benchmark) in data)
    {
        var validProcessingEntries = benchmark.BenchMarkData
            .Where(e => e.TimeToProcessBrushLocally.HasValue && e.TimeToUpdatePlots.HasValue)
            .ToList();

        if (!validProcessingEntries.Any())
            continue;

        var validPingEntries = benchmark.BenchMarkData
            .Where(e => !e.TimeToProcessBrushLocally.HasValue && !e.TimeToUpdatePlots.HasValue && e.Ping.HasValue)
            .ToList();

        var listTimeToProcessBrushLocally = validProcessingEntries.Select(e => e.TimeToProcessBrushLocally!.Value).ToList();
        var listTimeToUpdatePlots = validProcessingEntries.Select(e => e.TimeToUpdatePlots!.Value).ToList();
        var listPing = validPingEntries.Select(e => e.Ping!.Value).ToList();

        var statsTimeToProcessBrushLocally = ComputeStats(listTimeToProcessBrushLocally);
        var statsTimeToUpdatePlots = ComputeStats(listTimeToUpdatePlots);
        var statsPing = ComputeStats(listPing);

        var resultEntry = new Dictionary<string, object?>
        {
            ["ClientId"] = clientId,

            ["AvgTimeToProcessBrushLocally"] = statsTimeToProcessBrushLocally.Mean,
            ["MinTimeToProcessBrushLocally"] = statsTimeToProcessBrushLocally.Min,
            ["MaxTimeToProcessBrushLocally"] = statsTimeToProcessBrushLocally.Max,
            ["SDTimeToProcessBrushLocally"] = statsTimeToProcessBrushLocally.StandardDeviation,
            ["ListTimeToProcessBrushLocally"] = listTimeToProcessBrushLocally,

            ["AvgTimeToUpdatePlots"] = statsTimeToUpdatePlots.Mean,
            ["MinTimeToUpdatePlots"] = statsTimeToUpdatePlots.Min,
            ["MaxTimeToUpdatePlots"] = statsTimeToUpdatePlots.Max,
            ["SDTimeToUpdatePlots"] = statsTimeToUpdatePlots.StandardDeviation,
            ["ListTimeToUpdatePlots"] = listTimeToUpdatePlots,

            ["AvgPing"] = statsPing.Mean,
            ["MinPing"] = statsPing.Min,
            ["MaxPing"] = statsPing.Max,
            ["SDPing"] = statsPing.StandardDeviation,
            ["ListPing"] = listPing
        };

        if (includeTimeToProcess)
        {
            var listTimeToProcess = benchmark.BenchMarkData
                .Where(e => e.TimeToProcess.HasValue)
                .Select(e => e.TimeToProcess!.Value)
                .ToList();

            var statsTimeToProcess = ComputeStats(listTimeToProcess);

            resultEntry["AvgTimeToProcess"] = statsTimeToProcess.Mean;
            resultEntry["MinTimeToProcess"] = statsTimeToProcess.Min;
            resultEntry["MaxTimeToProcess"] = statsTimeToProcess.Max;
            resultEntry["SDTimeToProcess"] = statsTimeToProcess.StandardDeviation;
            resultEntry["ListTimeToProcess"] = listTimeToProcess;
        }

        result[clientId] = resultEntry;
    }

    return result;
}

private static Dictionary<string, double> ComputeOverallAverages(Dictionary<int, object> data)
{
    var aggregatedValues = new Dictionary<string, List<double>>();

    foreach (var entry in data.Values)
    {
        var dict = entry.GetType().GetProperties()
            .Where(p => p.PropertyType == typeof(double))
            .ToDictionary(p => p.Name, p => (double)p.GetValue(entry)!);

        foreach (var kvp in dict)
        {
            if (!aggregatedValues.ContainsKey(kvp.Key))
                aggregatedValues[kvp.Key] = new List<double>();

            aggregatedValues[kvp.Key].Add(kvp.Value);
        }
    }

    return aggregatedValues.ToDictionary(
        kvp => kvp.Key,
        kvp => kvp.Value.Average()
    );
}


private static Dictionary<int, object> ComputeAverages(Dictionary<int, ClientBenchmark> data, bool includeTimeToProcess)
{
    var result = new Dictionary<int, object>();

    foreach (var (clientId, benchmark) in data)
    {
        var validProcessingEntries = benchmark.BenchMarkData
            .Where(e => e.TimeToProcessBrushLocally.HasValue && e.TimeToUpdatePlots.HasValue)
            .ToList();

        if (!validProcessingEntries.Any()) 
            continue; // Skip this client if there are no valid processing entries

        var validPingEntries = benchmark.BenchMarkData
            .Where(e => !e.TimeToProcessBrushLocally.HasValue && !e.TimeToUpdatePlots.HasValue && e.Ping.HasValue)
            .ToList();

        var statsTimeToProcessBrushLocally = ComputeStats(validProcessingEntries.Select(e => e.TimeToProcessBrushLocally!.Value).ToList());
        var statsTimeToUpdatePlots = ComputeStats(validProcessingEntries.Select(e => e.TimeToUpdatePlots!.Value).ToList());
        var statsPing = ComputeStats(validPingEntries.Select(e => e.Ping!.Value).ToList());

        var resultEntry = new
        {
            ClientId = clientId,

            AvgTimeToProcessBrushLocally = statsTimeToProcessBrushLocally.Mean,
            MinTimeToProcessBrushLocally = statsTimeToProcessBrushLocally.Min,
            MaxTimeToProcessBrushLocally = statsTimeToProcessBrushLocally.Max,
            SDTimeToProcessBrushLocally = statsTimeToProcessBrushLocally.StandardDeviation,

            AvgTimeToUpdatePlots = statsTimeToUpdatePlots.Mean,
            MinTimeToUpdatePlots = statsTimeToUpdatePlots.Min,
            MaxTimeToUpdatePlots = statsTimeToUpdatePlots.Max,
            SDTimeToUpdatePlots = statsTimeToUpdatePlots.StandardDeviation,

            AvgPing = statsPing.Mean,
            MinPing = statsPing.Min,
            MaxPing = statsPing.Max,
            SDPing = statsPing.StandardDeviation
        };

        if (includeTimeToProcess)
        {
            var validTimeToProcessEntries = benchmark.BenchMarkData
                .Where(e => e.TimeToProcess.HasValue)
                .Select(e => e.TimeToProcess!.Value)
                .ToList();

            var statsTimeToProcess = ComputeStats(validTimeToProcessEntries);

            result[clientId] = new
            {
                resultEntry.ClientId,

                resultEntry.AvgTimeToProcessBrushLocally,
                resultEntry.MinTimeToProcessBrushLocally,
                resultEntry.MaxTimeToProcessBrushLocally,
                resultEntry.SDTimeToProcessBrushLocally,

                resultEntry.AvgTimeToUpdatePlots,
                resultEntry.MinTimeToUpdatePlots,
                resultEntry.MaxTimeToUpdatePlots,
                resultEntry.SDTimeToUpdatePlots,

                resultEntry.AvgPing,
                resultEntry.MinPing,
                resultEntry.MaxPing,
                resultEntry.SDPing,

                AvgTimeToProcess = statsTimeToProcess.Mean,
                MinTimeToProcess = statsTimeToProcess.Min,
                MaxTimeToProcess = statsTimeToProcess.Max,
                SDTimeToProcess = statsTimeToProcess.StandardDeviation
            };
        }
        else
        {
            result[clientId] = resultEntry;
        }
    }

    return result;
}

    private static (double Mean, double Min, double Max, double StandardDeviation) ComputeStats(List<double> values)
    {
        if (values.Count == 0)
        {
            return (double.NaN, double.NaN, double.NaN, double.NaN);
        }

        double mean = values.Average();
        double min = values.Min();
        double max = values.Max();
        double variance = values.Select(v => Math.Pow(v - mean, 2)).Average();
        double standardDeviation = Math.Sqrt(variance);

        return (mean, min, max, standardDeviation);
    }
    
    public void Reset()
    {
        _sentBrushTimings.Clear();
        _receivedBrushTimings.Clear();
        _referenceConfig = null;
        _sentBrushTimings = new Dictionary<int, ClientBenchmark>();
        _receivedBrushTimings = new Dictionary<int, ClientBenchmark>();
    }
}
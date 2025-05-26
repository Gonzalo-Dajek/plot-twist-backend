using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;
using plot_twist_back_end.Messages;

public class BenchmarkHandler
{
    // Ping ---------------------------- //
    private class PingEvent
    {
        public double ping { get; set; }
        public double TimeOfEvent { get; set; }
    }
    // Sent Brushes -------------------------------------------------------------------------------------------------- //
    private class sentEventEntry {
        public double? TimeToProcessBrushLocally { get; set; }
        public double? TimeToUpdatePlots { get; set; }
        public double? TimeToProcess { get; set; } 
        public double? Ping { get; set; }
        public long TimeOfEvent { get; set; } }
    
    private class activeClient {
        public int ClientId { get; set; }
        public List<PingEvent> PingEventList { get; set; } = new();
        public List<sentEventEntry> SentEventList { get; set; } = new(); }
    
    private Dictionary<int, activeClient> _sentBrushTimings = new();

    // Received Brushes ---------------------------------------------------------------------------------------------- //
    private class receivedEventEntry {
        public double? TimeToProcessBrushLocally { get; set; }
        public double? TimeToUpdatePlots { get; set; }
        public double? Ping { get; set; }
        public long TimeOfEvent { get; set; } }
    
    
    private class passiveClient {
        public int ClientId { get; set; }
        public List<PingEvent> PingEventList { get; set; } = new();
        public List<receivedEventEntry> ReceivedEventList { get; set; } = new(); }
    
    private Dictionary<int, passiveClient> _receivedBrushTimings = new();
    
    // Config ----------------------------------- //
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

        var activeClient = new activeClient { ClientId = clientId};
        _sentBrushTimings[clientId] = activeClient;
        var passiveClient = new passiveClient { ClientId = clientId};
        _receivedBrushTimings[clientId] = passiveClient;
    }

    public bool isClientInitialized(int clientId)
    {
        return _sentBrushTimings.ContainsKey(clientId);
    }

    public void StoreSentBrushTimings(int clientId, double? timeToProcessBrushLocally, double? timeToUpdatePlots, double? timeToProcess, double? ping)
    {
        if (!_sentBrushTimings.TryGetValue(clientId, out var activeClient))
        {
            return; 
        }

        activeClient.SentEventList.Add(new sentEventEntry
        {
            TimeToProcessBrushLocally = timeToProcessBrushLocally,
            TimeToUpdatePlots = timeToUpdatePlots,
            TimeToProcess = timeToProcess,
            TimeOfEvent = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-(long)(ping!/2)-(long)timeToUpdatePlots!-(long)timeToProcessBrushLocally!,
            Ping = ping,
        });
    }

    public void StoreReceivedBrushTimings(int clientId, double? timeToProcessBrushLocally, double? timeToUpdatePlots, double? ping)
    {
        if (!_receivedBrushTimings.TryGetValue(clientId, out var clientBenchmark))
        {
            return; 
        }

        clientBenchmark.ReceivedEventList.Add(new receivedEventEntry
        {
            TimeToProcessBrushLocally = timeToProcessBrushLocally,
            TimeToUpdatePlots = timeToUpdatePlots,
            TimeOfEvent = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Ping = ping,
        });
    }

    public void StorePing(int clientId, double pingMs, int sentOrReceived)
    {
        var isSent = sentOrReceived == 1;
    
        if ((isSent && _sentBrushTimings.ContainsKey(clientId)) || 
            (!isSent && _receivedBrushTimings.ContainsKey(clientId)))
        {
            var pingEvent = new PingEvent
            {
                ping = pingMs,
                TimeOfEvent = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            
            if (isSent)
            {
                _sentBrushTimings[clientId].PingEventList.Add(pingEvent);
            }
            else
            {
                _receivedBrushTimings[clientId].PingEventList.Add(pingEvent);
            }
        }
    }

    private class Stats
    {
        public double Avg { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double SD { get; set; }
    }

    // helper method to compute mean/min/max/population‑SD
    private static Stats CalculateStats(IEnumerable<double> values)
    {
        var arr = values as double[] ?? values.ToArray();
        if (!arr.Any())
            return new Stats { Avg = 0, Min = 0, Max = 0, SD = 0 };

        double avg = arr.Average();
        double min = arr.Min();
        double max = arr.Max();
        double sumSq = arr.Sum(v => (v - avg) * (v - avg));
        double sd = Math.Sqrt(sumSq / arr.Length);

        return new Stats { Avg = avg, Min = min, Max = max, SD = sd };
    }

    public void DownloadSummarizedData()
    {
        var config = _referenceConfig!.Value;
        string directoryPath = "BenchMarkResults";
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
            $"_bSize:{config.brushSize:0.###}" +
            $"_bSpeed:{config.stepSize:0.###}" +
            $"_clients:{config.numberOfClientBrushing}" +
            $"_sets:{config.numberOfDataSets}" +
            $"_duration:{config.testDuration}.json");

        // Utility function for stats
        static (double avg, double min, double max, double stddev) CalcStats(List<double> values)
        {
            if (values.Count == 0) return (0, 0, 0, 0);
            double avg = values.Average();
            double min = values.Min();
            double max = values.Max();
            double std = Math.Sqrt(values.Sum(v => Math.Pow(v - avg, 2)) / values.Count);
            return (avg, min, max, std);
        }

        // Per-client summaries (Sent)
        var sentBrushData = _sentBrushTimings.ToDictionary(
            kvp => kvp.Key,
            kvp =>
            {
                var client = kvp.Value;
                var timeToProcessBrushLocally = client.SentEventList
                    .Where(e => e.TimeToProcessBrushLocally.HasValue)
                    .Select(e => e.TimeToProcessBrushLocally!.Value).ToList();
                var timeToUpdatePlots = client.SentEventList
                    .Where(e => e.TimeToUpdatePlots.HasValue)
                    .Select(e => e.TimeToUpdatePlots!.Value).ToList();
                var timeToProcess = client.SentEventList
                    .Where(e => e.TimeToProcess.HasValue)
                    .Select(e => e.TimeToProcess!.Value).ToList();
                var pings = client.PingEventList.Select(p => p.ping).ToList();

                var (avgLoc, minLoc, maxLoc, stdLoc) = CalcStats(timeToProcessBrushLocally);
                var (avgUpd, minUpd, maxUpd, stdUpd) = CalcStats(timeToUpdatePlots);
                var (avgProc, minProc, maxProc, stdProc) = CalcStats(timeToProcess);
                var (avgPing, minPing, maxPing, stdPing) = CalcStats(pings);

                return new
                {
                    client.ClientId,
                    AvgTimeToProcessBrushLocally = avgLoc,
                    MinTimeToProcessBrushLocally = minLoc,
                    MaxTimeToProcessBrushLocally = maxLoc,
                    SDTimeToProcessBrushLocally = stdLoc,
                    AvgTimeToUpdatePlots = avgUpd,
                    MinTimeToUpdatePlots = minUpd,
                    MaxTimeToUpdatePlots = maxUpd,
                    SDTimeToUpdatePlots = stdUpd,
                    AvgPing = avgPing,
                    MinPing = minPing,
                    MaxPing = maxPing,
                    SDPing = stdPing,
                    AvgTimeToProcess = avgProc,
                    MinTimeToProcess = minProc,
                    MaxTimeToProcess = maxProc,
                    SDTimeToProcess = stdProc
                };
            });

        // Per-client summaries (Received)
        var receivedBrushData = _receivedBrushTimings.ToDictionary(
            kvp => kvp.Key,
            kvp =>
            {
                var client = kvp.Value;
                var timeToProcessBrushLocally = client.ReceivedEventList
                    .Where(e => e.TimeToProcessBrushLocally.HasValue)
                    .Select(e => e.TimeToProcessBrushLocally!.Value).ToList();
                var timeToUpdatePlots = client.ReceivedEventList
                    .Where(e => e.TimeToUpdatePlots.HasValue)
                    .Select(e => e.TimeToUpdatePlots!.Value).ToList();
                var pings = client.PingEventList.Select(p => p.ping).ToList();

                var (avgLoc, minLoc, maxLoc, stdLoc) = CalcStats(timeToProcessBrushLocally);
                var (avgUpd, minUpd, maxUpd, stdUpd) = CalcStats(timeToUpdatePlots);
                var (avgPing, minPing, maxPing, stdPing) = CalcStats(pings);

                return new
                {
                    client.ClientId,
                    AvgTimeToProcessBrushLocally = avgLoc,
                    MinTimeToProcessBrushLocally = minLoc,
                    MaxTimeToProcessBrushLocally = maxLoc,
                    SDTimeToProcessBrushLocally = stdLoc,
                    AvgTimeToUpdatePlots = avgUpd,
                    MinTimeToUpdatePlots = minUpd,
                    MaxTimeToUpdatePlots = maxUpd,
                    SDTimeToUpdatePlots = stdUpd,
                    AvgPing = avgPing,
                    MinPing = minPing,
                    MaxPing = maxPing,
                    SDPing = stdPing
                };
            });

        // Averages of all sent/received clients
        static List<double> AvgStatsAcrossClients(
            Dictionary<int, object> statsPerClient,
            Func<object, double> selector
        )
            => statsPerClient.Values.Select(selector).ToList();

        Dictionary<string, double> GetAvgOfStats(string keyName, Func<dynamic, double> selector)
        {
            var values = sentBrushData.Values.Select(selector).ToList();
            return new Dictionary<string, double> { [keyName] = values.Count > 0 ? values.Average() : 0 };
        }

        var allSentTimeToProcessBrushLocally = _sentBrushTimings.Values.SelectMany(c => c.SentEventList.Where(e => e.TimeToProcessBrushLocally.HasValue).Select(e => e.TimeToProcessBrushLocally!.Value)).ToList();
        var allSentTimeToUpdatePlots = _sentBrushTimings.Values.SelectMany(c => c.SentEventList.Where(e => e.TimeToUpdatePlots.HasValue).Select(e => e.TimeToUpdatePlots!.Value)).ToList();
        var allSentTimeToProcess = _sentBrushTimings.Values.SelectMany(c => c.SentEventList.Where(e => e.TimeToProcess.HasValue).Select(e => e.TimeToProcess!.Value)).ToList();
        var allSentPings = _sentBrushTimings.Values.SelectMany(c => c.PingEventList.Select(p => p.ping)).ToList();
        var (avgSL, minSL, maxSL, stdSL) = CalcStats(allSentTimeToProcessBrushLocally);
        var (avgSU, minSU, maxSU, stdSU) = CalcStats(allSentTimeToUpdatePlots);
        var (avgST, minST, maxST, stdST) = CalcStats(allSentTimeToProcess);
        var (avgSP, minSP, maxSP, stdSP) = CalcStats(allSentPings);

        var AvgSentBrushTimings = new
        {
            AvgTimeToProcessBrushLocally = avgSL,
            MinTimeToProcessBrushLocally = minSL,
            MaxTimeToProcessBrushLocally = maxSL,
            SDTimeToProcessBrushLocally = stdSL,
            AvgTimeToUpdatePlots = avgSU,
            MinTimeToUpdatePlots = minSU,
            MaxTimeToUpdatePlots = maxSU,
            SDTimeToUpdatePlots = stdSU,
            AvgPing = avgSP,
            MinPing = minSP,
            MaxPing = maxSP,
            SDPing = stdSP,
            AvgTimeToProcess = avgST,
            MinTimeToProcess = minST,
            MaxTimeToProcess = maxST,
            SDTimeToProcess = stdST
        };

        var allReceivedTimeToProcessBrushLocally = _receivedBrushTimings.Values.SelectMany(c => c.ReceivedEventList.Where(e => e.TimeToProcessBrushLocally.HasValue).Select(e => e.TimeToProcessBrushLocally!.Value)).ToList();
        var allReceivedTimeToUpdatePlots = _receivedBrushTimings.Values.SelectMany(c => c.ReceivedEventList.Where(e => e.TimeToUpdatePlots.HasValue).Select(e => e.TimeToUpdatePlots!.Value)).ToList();
        var allReceivedPings = _receivedBrushTimings.Values.SelectMany(c => c.PingEventList.Select(p => p.ping)).ToList();
        var (avgRL, minRL, maxRL, stdRL) = CalcStats(allReceivedTimeToProcessBrushLocally);
        var (avgRU, minRU, maxRU, stdRU) = CalcStats(allReceivedTimeToUpdatePlots);
        var (avgRP, minRP, maxRP, stdRP) = CalcStats(allReceivedPings);

        var AvgReceivedBrushTimings = new
        {
            AvgTimeToProcessBrushLocally = avgRL,
            MinTimeToProcessBrushLocally = minRL,
            MaxTimeToProcessBrushLocally = maxRL,
            SDTimeToProcessBrushLocally = stdRL,
            AvgTimeToUpdatePlots = avgRU,
            MinTimeToUpdatePlots = minRU,
            MaxTimeToUpdatePlots = maxRU,
            SDTimeToUpdatePlots = stdRU,
            AvgPing = avgRP,
            MinPing = minRP,
            MaxPing = maxRP,
            SDPing = stdRP
        };

        // Events per client
        var EventsPerClient = new Dictionary<int, object>();
        foreach (var kvp in _sentBrushTimings)
        {
            var id = kvp.Key;
            EventsPerClient[id] = new
            {
                PingEvents = kvp.Value.PingEventList.OrderBy(e => e.TimeOfEvent).ToList(),
                SentBrushEvents = kvp.Value.SentEventList.OrderBy(e => e.TimeOfEvent).ToList(),
                ReceivedBrushEvents = _receivedBrushTimings.ContainsKey(id)
                    ? _receivedBrushTimings[id].ReceivedEventList.OrderBy(e => e.TimeOfEvent).ToList()
                    : new List<receivedEventEntry>()
            };
        }

        // All events (merged)
        var AllEvents = new List<(double TimeOfEvent, string Type, int ClientId, object Event)>();

        foreach (var kvp in _sentBrushTimings)
        {
            var id = kvp.Key;

            // Ping events (TimeOfEvent is already double, but we cast for consistency)
            AllEvents.AddRange(
                kvp.Value.PingEventList
                    .Select(e => ((double)e.TimeOfEvent, "Ping",    id, (object)e))
            );

            // Sent‐brush events (TimeOfEvent is long, so we must cast)
            AllEvents.AddRange(
                kvp.Value.SentEventList
                    .Select(e => ((double)e.TimeOfEvent, "Sent",    id, (object)e))
            );
        }
        foreach (var kvp in _receivedBrushTimings)
        {
            var id = kvp.Key;
            AllEvents.AddRange(
                kvp.Value.PingEventList
                    .Select(e => ((double)e.TimeOfEvent, "Ping",    id, (object)e))
            );
            AllEvents.AddRange(
                kvp.Value.ReceivedEventList
                    .Select(e => ((double)e.TimeOfEvent, "Received", id, (object)e))
            );
        }
        
        var orderedEvents = AllEvents.OrderBy(e => e.TimeOfEvent).Select(e => new
        {
            e.TimeOfEvent,
            e.Type,
            e.ClientId,
            Event = e.Event
        }).ToList();

        // Full data lists
        var SentBrushTimingsPerClientFull = _sentBrushTimings.ToDictionary(
            kvp => kvp.Key,
            kvp => new
            {
                kvp.Value.ClientId,
                ListTimeToProcessBrushLocally = kvp.Value.SentEventList.Where(e => e.TimeToProcessBrushLocally.HasValue).Select(e => e.TimeToProcessBrushLocally!.Value).ToList(),
                ListTimeToUpdatePlots = kvp.Value.SentEventList.Where(e => e.TimeToUpdatePlots.HasValue).Select(e => e.TimeToUpdatePlots!.Value).ToList(),
                ListTimeToProcess = kvp.Value.SentEventList.Where(e => e.TimeToProcess.HasValue).Select(e => e.TimeToProcess!.Value).ToList(),
                ListPing = kvp.Value.PingEventList.Select(e => e.ping).ToList()
            });

        var ReceivedBrushTimingsPerClientFull = _receivedBrushTimings.ToDictionary(
            kvp => kvp.Key,
            kvp => new
            {
                kvp.Value.ClientId,
                ListTimeToProcessBrushLocally = kvp.Value.ReceivedEventList.Where(e => e.TimeToProcessBrushLocally.HasValue).Select(e => e.TimeToProcessBrushLocally!.Value).ToList(),
                ListTimeToUpdatePlots = kvp.Value.ReceivedEventList.Where(e => e.TimeToUpdatePlots.HasValue).Select(e => e.TimeToUpdatePlots!.Value).ToList(),
                ListPing = kvp.Value.PingEventList.Select(e => e.ping).ToList()
            });

        var summarizedData = new
        {
            AvgSentBrushTimings,
            AvgReceivedBrushTimings,
            SentBrushTimingsPerClient = sentBrushData,
            ReceivedBrushTimingsPerClient = receivedBrushData,
            EventsPerClient,
            AllEvents = orderedEvents,
            SentBrushTimingsPerClientFull,
            ReceivedBrushTimingsPerClientFull
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
    
    public void Reset()
    {
        _sentBrushTimings.Clear();
        _receivedBrushTimings.Clear();
        _referenceConfig = null;
        _sentBrushTimings = new Dictionary<int, activeClient>();
        _receivedBrushTimings = new Dictionary<int, passiveClient>();
    }
}
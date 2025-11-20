using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;
using plot_twist_back_end.Messages;

public class Benchmark
{
    // store times internally in microseconds (sub-ms). Externally (JSON/statistics) we convert back to ms (double).
    private static long NowMicroseconds()
    {
        // Unix ms * 1000 + fractional microseconds from DateTime ticks (1 tick = 100ns)
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L
               + (DateTime.UtcNow.Ticks % TimeSpan.TicksPerMillisecond) / 10L;
    }

    // Ping ---------------------------- //
    private class PingEvent
    {
        // ping stored in microseconds (ms * 1000)
        public long PingSubMs { get; set; }
        // time of event stored as unix epoch microseconds
        public long TimeOfEvent { get; set; }
    }
    
    // Acks ---------------------------------------------------------------------------------------------------------- //
    private class PendingEvent
    {
        public bool WasSentBrush { get; set; }
        // durations stored in microseconds (ms * 1000)
        public long? TimeToProcessBrushLocally { get; set; }
        public long? TimeToProcessBrushTime { get; set; } // timestamp (microseconds)
        public long? TimeToUpdatePlots { get; set; }
        public long? TimeToProcessBrush { get; set; }
    }
    
    // Key = (clientId, brushId); Value = the buffered times
    private ConcurrentDictionary<(int clientId, long brushId), PendingEvent> _pendingEvents = new();
    
    // Sent Brushes -------------------------------------------------------------------------------------------------- //
    private class sentEventEntry {
        public long? TimeToProcessBrushLocally { get; set; } // microseconds
        public long? TimeToUpdatePlots { get; set; } // microseconds
        public long? TimeToProcess { get; set; }  // microseconds
        public long? PingSubMs { get; set; } // microseconds
        public long TimeOfEvent { get; set; } // unix microseconds
        public long TimeOfReceivedAck { get; set; } // unix microseconds
    }
    
    private class activeClient {
        public int ClientId { get; set; }
        public List<PingEvent> PingEventList { get; set; } = new();
        public List<sentEventEntry> SentEventList { get; set; } = new();
    }
    
    private ConcurrentDictionary<int, activeClient> _sentBrushTimings = new();

    // Received Brushes ---------------------------------------------------------------------------------------------- //
    private class receivedEventEntry {
        public long? TimeToProcessBrushLocally { get; set; } // microseconds
        public long? TimeToUpdatePlots { get; set; } // microseconds
        public long? PingSubMs { get; set; } // microseconds
        public long TimeOfEvent { get; set; } // unix microseconds
    }
    
    
    private class passiveClient {
        public int ClientId { get; set; }
        public List<PingEvent> PingEventList { get; set; } = new();
        public List<receivedEventEntry> ReceivedEventList { get; set; } = new();
    }
    
    private ConcurrentDictionary<int, passiveClient> _receivedBrushTimings = new();
    
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
    
    // External callers should pass durations in microseconds (ms * 1000)
    public void RecordProcessBrushInServer(int clientId, long brushId, long timeToProcessSubMs)
    {
        var key = (clientId, brushId);
        var pe = new PendingEvent { WasSentBrush = true, TimeToProcessBrush = timeToProcessSubMs};
        _pendingEvents[key] = pe; 
    }
    
    // timeToUpdatePlotsSubMs in microseconds
    public void RecordUpdatePlots(int clientId, long brushId, long timeToUpdatePlotsSubMs, bool wasSentBrush)
    {
        var key = (clientId, brushId);
        if (!_pendingEvents.TryGetValue(key, out var pe))
        {
            pe = new PendingEvent { WasSentBrush = wasSentBrush };
            _pendingEvents[key] = pe; 
        }

        pe.WasSentBrush = wasSentBrush;
        pe.TimeToUpdatePlots = timeToUpdatePlotsSubMs;
        
        if (pe.WasSentBrush)
        {
            StoreSentBrushTimings(clientId,
                pe.TimeToProcessBrushLocally,
                timeToUpdatePlotsSubMs,
                pe.TimeToProcessBrush,
                -1,
                pe.TimeToProcessBrushTime ?? -1,
                NowMicroseconds());
        }
        else
        {
            StoreReceivedBrushTimings(clientId, pe.TimeToProcessBrushLocally, pe.TimeToUpdatePlots, -1, NowMicroseconds());
        }
    }
    
    // timeToProcessBrushSubMs in microseconds
    public void RecordProcessBrush(int clientId, long brushId, long timeToProcessBrushSubMs, bool wasSentBrush)
    {
        var key = (clientId, brushId);
        if (!_pendingEvents.TryGetValue(key, out var pe))
        {
            pe = new PendingEvent { WasSentBrush = wasSentBrush };
            _pendingEvents[key] = pe; 
        }
        else
        {
            pe.WasSentBrush = wasSentBrush; 
        }

        pe.TimeToProcessBrushLocally = timeToProcessBrushSubMs;
        pe.TimeToProcessBrushTime = NowMicroseconds();
    }

    // Store durations/pings in microseconds (long)
    public void StoreSentBrushTimings(int clientId, long? timeToProcessBrushLocallySubMs, long? timeToUpdatePlotsSubMs, long? timeToProcessSubMs, long? pingSubMs, long timeOfEventSubMs, long timeOfReceivedAckSubMs)
    {
        if (!_sentBrushTimings.TryGetValue(clientId, out var activeClient))
        {
            return; 
        }

        activeClient.SentEventList.Add(new sentEventEntry
        {
            TimeToProcessBrushLocally = timeToProcessBrushLocallySubMs,
            TimeToUpdatePlots = timeToUpdatePlotsSubMs,
            TimeToProcess = timeToProcessSubMs,
            TimeOfEvent = timeOfEventSubMs,
            PingSubMs = pingSubMs,
            TimeOfReceivedAck = timeOfReceivedAckSubMs
        });
    }

    public void StoreReceivedBrushTimings(int clientId, long? timeToProcessBrushLocallySubMs, long? timeToUpdatePlotsSubMs, long? pingSubMs, long timeOfEventSubMs)
    {
        if (!_receivedBrushTimings.TryGetValue(clientId, out var clientBenchmark))
        {
            return; 
        }

        clientBenchmark.ReceivedEventList.Add(new receivedEventEntry
        {
            TimeToProcessBrushLocally = timeToProcessBrushLocallySubMs,
            TimeToUpdatePlots = timeToUpdatePlotsSubMs,
            TimeOfEvent = timeOfEventSubMs,
            PingSubMs = pingSubMs,
        });
    }

    // pingSubMs in microseconds
    public void StorePing(int clientId, long pingSubMs, int sentOrReceived)
    {
        var isSent = sentOrReceived == 1;
    
        if ((isSent && _sentBrushTimings.ContainsKey(clientId)) || 
            (!isSent && _receivedBrushTimings.ContainsKey(clientId)))
        {
            var pingEvent = new PingEvent
            {
                PingSubMs = pingSubMs,
                TimeOfEvent = NowMicroseconds()
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

    // helper method to compute mean/min/max/population-SD for doubles (ms)
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

        // snapshot to avoid concurrent-modification during enumeration
        var sentSnapshot     = _sentBrushTimings.ToArray();
        var receivedSnapshot = _receivedBrushTimings.ToArray();

        // Utility function for stats from lists of microseconds -> returns (ms)
        static (double avg, double min, double max, double stddev) CalcStatsFromSubMs(List<long> valuesSubMs)
        {
            if (valuesSubMs.Count == 0) return (0, 0, 0, 0);
            // convert to ms double
            var dbl = valuesSubMs.Select(v => v / 1000.0).ToArray();
            double avg = dbl.Average();
            double min = dbl.Min();
            double max = dbl.Max();
            double std = Math.Sqrt(dbl.Sum(v => Math.Pow(v - avg, 2)) / dbl.Length);
            return (avg, min, max, std);
        }

        // Per-client summaries (Sent)
        var sentBrushData = sentSnapshot.ToDictionary(
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
                var pings = client.PingEventList.Select(p => p.PingSubMs).ToList();

                var (avgLoc, minLoc, maxLoc, stdLoc) = CalcStatsFromSubMs(timeToProcessBrushLocally);
                var (avgUpd, minUpd, maxUpd, stdUpd) = CalcStatsFromSubMs(timeToUpdatePlots);
                var (avgProc, minProc, maxProc, stdProc) = CalcStatsFromSubMs(timeToProcess);
                var (avgPing, minPing, maxPing, stdPing) = CalcStatsFromSubMs(pings);

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
        var receivedBrushData = receivedSnapshot.ToDictionary(
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
                var pings = client.PingEventList.Select(p => p.PingSubMs).ToList();

                var (avgLoc, minLoc, maxLoc, stdLoc) = CalcStatsFromSubMs(timeToProcessBrushLocally);
                var (avgUpd, minUpd, maxUpd, stdUpd) = CalcStatsFromSubMs(timeToUpdatePlots);
                var (avgPing, minPing, maxPing, stdPing) = CalcStatsFromSubMs(pings);

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
        var allSentTimeToProcessBrushLocally = sentSnapshot.SelectMany(k => k.Value.SentEventList
            .Where(e => e.TimeToProcessBrushLocally.HasValue)
            .Select(e => e.TimeToProcessBrushLocally!.Value)).ToList();
        var allSentTimeToUpdatePlots = sentSnapshot.SelectMany(k => k.Value.SentEventList
            .Where(e => e.TimeToUpdatePlots.HasValue)
            .Select(e => e.TimeToUpdatePlots!.Value)).ToList();
        var allSentTimeToProcess = sentSnapshot.SelectMany(k => k.Value.SentEventList
            .Where(e => e.TimeToProcess.HasValue)
            .Select(e => e.TimeToProcess!.Value)).ToList();
        var allSentPings = sentSnapshot.SelectMany(k => k.Value.PingEventList.Select(p => p.PingSubMs)).ToList();
        var (avgSL, minSL, maxSL, stdSL) = CalcStatsFromSubMs(allSentTimeToProcessBrushLocally);
        var (avgSU, minSU, maxSU, stdSU) = CalcStatsFromSubMs(allSentTimeToUpdatePlots);
        var (avgST, minST, maxST, stdST) = CalcStatsFromSubMs(allSentTimeToProcess);
        var (avgSP, minSP, maxSP, stdSP) = CalcStatsFromSubMs(allSentPings);

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

        var allReceivedTimeToProcessBrushLocally = receivedSnapshot.SelectMany(k => k.Value.ReceivedEventList
            .Where(e => e.TimeToProcessBrushLocally.HasValue)
            .Select(e => e.TimeToProcessBrushLocally!.Value)).ToList();
        var allReceivedTimeToUpdatePlots = receivedSnapshot.SelectMany(k => k.Value.ReceivedEventList
            .Where(e => e.TimeToUpdatePlots.HasValue)
            .Select(e => e.TimeToUpdatePlots!.Value)).ToList();
        var allReceivedPings = receivedSnapshot.SelectMany(k => k.Value.PingEventList.Select(p => p.PingSubMs)).ToList();
        var (avgRL, minRL, maxRL, stdRL) = CalcStatsFromSubMs(allReceivedTimeToProcessBrushLocally);
        var (avgRU, minRU, maxRU, stdRU) = CalcStatsFromSubMs(allReceivedTimeToUpdatePlots);
        var (avgRP, minRP, maxRP, stdRP) = CalcStatsFromSubMs(allReceivedPings);

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
        foreach (var kvp in sentSnapshot)
        {
            var id = kvp.Key;
            // map stored microsecond values to ms (double) for JSON
            var pingEvents = kvp.Value.PingEventList
                .OrderBy(e => e.TimeOfEvent)
                .Select(e => new { ping = e.PingSubMs / 1000.0, TimeOfEvent = e.TimeOfEvent / 1000.0 })
                .ToList();
            var sentBrushEvents = kvp.Value.SentEventList
                .OrderBy(e => e.TimeOfEvent)
                .Select(e => new {
                    TimeToProcessBrushLocally = e.TimeToProcessBrushLocally.HasValue ? (double?) (e.TimeToProcessBrushLocally.Value / 1000.0) : null,
                    TimeToUpdatePlots = e.TimeToUpdatePlots.HasValue ? (double?)(e.TimeToUpdatePlots.Value / 1000.0) : null,
                    TimeToProcess = e.TimeToProcess.HasValue ? (double?)(e.TimeToProcess.Value / 1000.0) : null,
                    Ping = e.PingSubMs.HasValue ? (double?)(e.PingSubMs.Value / 1000.0) : null,
                    TimeOfEvent = e.TimeOfEvent / 1000.0,
                    TimeOfReceivedAck = e.TimeOfReceivedAck / 1000.0
                }).ToList();

            var receivedBrushEvents = receivedSnapshot.Any(x => x.Key == id)
                ? receivedSnapshot.First(x => x.Key == id).Value.ReceivedEventList
                    .OrderBy(e => e.TimeOfEvent)
                    .Select(e => (object)new {
                        TimeToProcessBrushLocally = e.TimeToProcessBrushLocally.HasValue ? (double?)(e.TimeToProcessBrushLocally.Value / 1000.0) : null,
                        TimeToUpdatePlots = e.TimeToUpdatePlots.HasValue ? (double?)(e.TimeToUpdatePlots.Value / 1000.0) : null,
                        Ping = e.PingSubMs.HasValue ? (double?)(e.PingSubMs.Value / 1000.0) : null,
                        TimeOfEvent = e.TimeOfEvent / 1000.0
                    }).ToList()
                : new List<object>();

            EventsPerClient[id] = new
            {
                PingEvents = pingEvents,
                SentBrushEvents = sentBrushEvents,
                ReceivedBrushEvents = receivedBrushEvents
            };
        }

        // All events (merged)
        var AllEvents = new List<(long TimeOfEvent, string Type, int ClientId, object Event)>();
        foreach (var kvp in sentSnapshot)
        {
            var id = kvp.Key;
            AllEvents.AddRange(kvp.Value.PingEventList.Select(e => (e.TimeOfEvent, "Ping", id, (object)new { ping = e.PingSubMs / 1000.0, TimeOfEvent = e.TimeOfEvent / 1000.0 })));
            AllEvents.AddRange(kvp.Value.SentEventList.Select(e => (e.TimeOfEvent, "Sent", id, (object)new {
                TimeToProcessBrushLocally = e.TimeToProcessBrushLocally.HasValue ? (double?)(e.TimeToProcessBrushLocally.Value / 1000.0) : null,
                TimeToUpdatePlots = e.TimeToUpdatePlots.HasValue ? (double?)(e.TimeToUpdatePlots.Value / 1000.0) : null,
                TimeToProcess = e.TimeToProcess.HasValue ? (double?)(e.TimeToProcess.Value / 1000.0) : null,
                Ping = e.PingSubMs.HasValue ? (double?)(e.PingSubMs.Value / 1000.0) : null,
                TimeOfEvent = e.TimeOfEvent / 1000.0,
                TimeOfReceivedAck = e.TimeOfReceivedAck / 1000.0
            })));
        }
        foreach (var kvp in receivedSnapshot)
        {
            var id = kvp.Key;
            AllEvents.AddRange(kvp.Value.PingEventList.Select(e => (e.TimeOfEvent, "Ping", id, (object)new { ping = e.PingSubMs / 1000.0, TimeOfEvent = e.TimeOfEvent / 1000.0 })));
            AllEvents.AddRange(kvp.Value.ReceivedEventList.Select(e => (e.TimeOfEvent, "Received", id, (object)new {
                TimeToProcessBrushLocally = e.TimeToProcessBrushLocally.HasValue ? (double?)(e.TimeToProcessBrushLocally.Value / 1000.0) : null,
                TimeToUpdatePlots = e.TimeToUpdatePlots.HasValue ? (double?)(e.TimeToUpdatePlots.Value / 1000.0) : null,
                Ping = e.PingSubMs.HasValue ? (double?)(e.PingSubMs.Value / 1000.0) : null,
                TimeOfEvent = e.TimeOfEvent / 1000.0
            })));
        }
        var orderedEvents = AllEvents.OrderBy(e => e.TimeOfEvent).Select(e => new
        {
            TimeOfEvent = e.TimeOfEvent / 1000.0,
            e.Type,
            e.ClientId,
            Event = e.Event
        }).ToList();

        var SentBrushTimingsPerClientFull = sentSnapshot.ToDictionary(
            kvp => kvp.Key,
            kvp => new
            {
                kvp.Value.ClientId,
                ListTimeToProcessBrushLocally = kvp.Value.SentEventList.Where(e => e.TimeToProcessBrushLocally.HasValue).Select(e => e.TimeToProcessBrushLocally!.Value / 1000.0).ToList(),
                ListTimeToUpdatePlots = kvp.Value.SentEventList.Where(e => e.TimeToUpdatePlots.HasValue).Select(e => e.TimeToUpdatePlots!.Value / 1000.0).ToList(),
                ListTimeToProcess = kvp.Value.SentEventList.Where(e => e.TimeToProcess.HasValue).Select(e => e.TimeToProcess!.Value / 1000.0).ToList(),
                ListPing = kvp.Value.PingEventList.Select(e => e.PingSubMs / 1000.0).ToList()
            });

        var ReceivedBrushTimingsPerClientFull = receivedSnapshot.ToDictionary(
            kvp => kvp.Key,
            kvp => new
            {
                kvp.Value.ClientId,
                ListTimeToProcessBrushLocally = kvp.Value.ReceivedEventList.Where(e => e.TimeToProcessBrushLocally.HasValue).Select(e => e.TimeToProcessBrushLocally!.Value / 1000.0).ToList(),
                ListTimeToUpdatePlots = kvp.Value.ReceivedEventList.Where(e => e.TimeToUpdatePlots.HasValue).Select(e => e.TimeToUpdatePlots!.Value / 1000.0).ToList(),
                ListPing = kvp.Value.PingEventList.Select(e => e.PingSubMs / 1000.0).ToList()
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
        _sentBrushTimings = new ConcurrentDictionary<int, activeClient>();
        _receivedBrushTimings = new ConcurrentDictionary<int, passiveClient>();
    }
}

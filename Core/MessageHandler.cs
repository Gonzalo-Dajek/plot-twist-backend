using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using plot_twist_back_end.Messages;

namespace plot_twist_back_end.Core;

public class MessageHandler
{
    public ClientsSelections selections { get; }
    public CrossDataSetLinks links { get; }
    public WebSocketCoordinator wsCoordinator{ get; }
    public Benchmark benchmark;
    private readonly ConcurrentDictionary<int, Channel<Message>> _selectionChannels = new();
    private bool _isUpdating = false;
    
    public MessageHandler(
        ClientsSelections selections, 
        CrossDataSetLinks links, 
        WebSocketCoordinator wsCoordinator, 
        Benchmark benchmark)
    {
        this.selections = selections;
        this.links = links;
        this.wsCoordinator = wsCoordinator;
        this.benchmark = benchmark;
    }
    

    public async Task HandleMessage(string message, int socketId)
    {
    var clientMessage = JsonSerializer.Deserialize<Message>(message);

    switch (clientMessage.type)
    {
        case "link":
            links.UpdateClientsLinks(clientMessage.links!, clientMessage.linksOperator!);
            links.updateCrossDataSetSelection();
            links.broadcastClientsLinks();
            selections.ThrottledBroadcastClientsSelections(0);
            break;

        case "selection":
            // var timer = Stopwatch.StartNew();
            // fast-path: write into bounded channel (capacity = 1, DropOldest)
            if (_selectionChannels.TryGetValue(socketId, out var ch))
            {
                // TryWrite will succeed and if the channel is full it will drop the oldest item
                ch.Writer.TryWrite(clientMessage);
            }
            else
            {
                // fallback: no channel (shouldn't normally happen) -> handle immediately
                selections.UpdateClientSelection(socketId, clientMessage.clientsSelections![0]);
                links.updateCrossDataSetSelection();
                selections.ThrottledBroadcastClientsSelections(0);
            }
            // timer.Stop();
            // Console.WriteLine($"selection took {timer.ElapsedMilliseconds} ms");
            break;

        case "addClient":
            wsCoordinator.InitializeClient(socketId);
            selections.AddClient(socketId);

            // create per-socket bounded channel that keeps only latest message
            var options = new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            };
            var channel = Channel.CreateBounded<Message>(options);
            _selectionChannels[socketId] = channel;

            // Start the background processor (fire-and-forget, but observe exceptions)
            _ = Task.Run(() => ProcessSelectionChannelAsync(socketId, channel.Reader));

            links.broadcastClientsLinks();
            selections.ThrottledBroadcastClientsSelections(0); // TODO: separate into broadcast selections and broadcast cross selections
            break;

        case "addDataSet":
            links.AddDataset(clientMessage.dataSet![0]);
            links.broadcastClientsLinks();
            break;

        case "BenchMark":
            break;
    }
}

    private async Task ProcessSelectionChannelAsync(int socketId, ChannelReader<Message> reader)
    {
        try
        {
            await foreach (var msg in reader.ReadAllAsync())
            {
                // Only runs for the last message(s) that survived the bounded buffer
                selections.UpdateClientSelection(socketId, msg.clientsSelections![0]);
                links.updateCrossDataSetSelection();
                selections.ThrottledBroadcastClientsSelections(0);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception in selection processor for {socketId}: {ex}");
        }
    }

    public void closeChannel(int socketId)
    {
        if (_selectionChannels.TryRemove(socketId, out var chToClose))
        {
            chToClose.Writer.TryComplete(); // stop the background reader
        }
    }
    
    //
    // public async void HandleMessage(string message, int socketId)
    // {
    //     var clientMessage = JsonSerializer.Deserialize<Message>(message);
    //     string jsonString = JsonSerializer.Serialize(clientMessage, new JsonSerializerOptions { WriteIndented = true });
    //     // Console.WriteLine("Message received: ");
    //     // Console.WriteLine(jsonString);
    //     // Console.WriteLine("--------------------------------------------------------------------");
    //     
    //     switch (clientMessage.type) {
    //         case "link":
    //             links.UpdateClientsLinks(clientMessage.links!, clientMessage.linksOperator!);
    //             links.updateCrossDataSetSelection();
    //             links.broadcastClientsLinks();
    //             selections.ThrottledBroadcastClientsSelections(0);
    //             break;
    //         case "selection":
    //             selections.UpdateClientSelection(socketId, clientMessage.clientsSelections![0]);
    //             
    //             links.updateCrossDataSetSelection();
    //             selections.ThrottledBroadcastClientsSelections(0);
    //             break;
    //         case "addClient":
    //             wsCoordinator.InitializeClient(socketId);
    //             selections.AddClient(socketId);
    //             links.broadcastClientsLinks();
    //             selections.ThrottledBroadcastClientsSelections(0);
    //             break;
    //         case "addDataSet":
    //             links.AddDataset(clientMessage.dataSet![0]); // TODO: handle multiple
    //             links.broadcastClientsLinks();
    //             break;
    //         case "BenchMark":
    //             // HandleBenchmarkAction(clientMessage, socketId, benchmark, wsCoordinator, links, selections);
    //             break;
    //     }
    // }
    //
    // private static void HandleBenchmarkAction(Message clientMessage, int socketId, Benchmark benchmark, WebSocketCoordinator wsc, CrossDataSetLinks lh, ClientsSelections bh)
    // {
    //     var serverResponse = new Message();
    //     serverResponse.type = "BenchMark";
    //
    //     switch (clientMessage.benchMark?.action)
    //     {
    //         case "addClientBenchMark":
    //             var clientInfo = clientMessage.benchMark?.clientInfo ?? throw new InvalidOperationException("clientInfo should not be null");
    //             int id = clientMessage.benchMark?.clientId ?? throw new InvalidOperationException("clientId should not be null");
    //             Console.WriteLine($"addClientBenchMark: Adding client {id} to bench mark");
    //             benchmark.AddClient(id, clientInfo);
    //
    //             wsc.InitializeClient(socketId);
    //             // TODO: lh.AddDataset(clientMessage.dataSet?.name!);
    //             bh.AddClient(socketId);
    //             break;
    //
    //         case "start":
    //             Console.WriteLine("start: Sending benchmark start");
    //             serverResponse.benchMark = new BenchMarkMsg()
    //             {
    //                 action = "start",
    //             };
    //             // TODO: bh.updateClientsLinks(lh, wsc);
    //             _ = wsc.BroadcastMessage(serverResponse, 0);
    //             break;
    //         
    //         case "end":
    //             benchmark.DownloadSummarizedData();
    //             benchmark.Reset();
    //             serverResponse.benchMark = new BenchMarkMsg()
    //             {
    //                 action = "end",
    //             };
    //             _ = wsc.BroadcastMessage(serverResponse, 0);
    //             break;
    //
    //         case "processBrushInServer":
    //             {
    //                 var clientId = clientMessage.benchMark?.clientId ?? -1;
    //                 var brushId = clientMessage.benchMark?.brushId ?? -1;
    //
    //                 var stopwatch = Stopwatch.StartNew();
    //                 bh.UpdateClientSelection(socketId, clientMessage.benchMark?.range![0] ?? new ClientSelection());
    //                 bh.ThrottledBroadcastClientsSelections(socketId);
    //                 stopwatch.Stop();
    //                 
    //                 var timeToProcess = stopwatch.Elapsed.TotalMilliseconds;
    //                 
    //                 benchmark.RecordProcessBrushInServer(clientId, brushId, timeToProcess);
    //             }
    //             break;
    //         
    //         case "updateIndexes":
    //             {
    //                 // string status = (clientMessage.benchMark?.isActiveBrush ?? false) ? "Sent" : "Received";
    //                 // Console.WriteLine($"Index|ClientId:{clientMessage.benchMark?.clientId!}|BrushId:{clientMessage.benchMark?.brushId!}|{status}");
    //                 
    //                 var clientId = clientMessage.benchMark?.clientId ?? -1;
    //                 var brushId = clientMessage.benchMark?.brushId ?? -1;
    //                 var timeToProcessBrush = clientMessage.benchMark?.timeToProcessBrushLocally ?? -1;
    //                 var wasSentBrush = clientMessage.benchMark?.isActiveBrush;
    //                 
    //                 benchmark.RecordProcessBrush(clientId, brushId, timeToProcessBrush, wasSentBrush ?? true);
    //             }
    //             break;
    //
    //         case "updatePlots":
    //             {
    //                 // string status = (clientMessage.benchMark?.isActiveBrush ?? false)  ? "Sent" : "Received";
    //                 // Console.WriteLine($"Plots|ClientId:{clientMessage.benchMark?.clientId!}|BrushId:{clientMessage.benchMark?.brushId!}|{status}");
    //                 
    //                 var clientId = clientMessage.benchMark?.clientId ?? -1;
    //                 var brushId = clientMessage.benchMark?.brushId ?? -1;
    //                 var timeToUpdatePlots = clientMessage.benchMark?.timeToUpdatePlots ?? -1;
    //                 var wasSentBrush = clientMessage.benchMark?.isActiveBrush;
    //                 
    //
    //                 benchmark.RecordUpdatePlots(clientId, brushId, timeToUpdatePlots, wasSentBrush ?? true);
    //             }
    //             break;
    //     }
    // }
    //
    
}
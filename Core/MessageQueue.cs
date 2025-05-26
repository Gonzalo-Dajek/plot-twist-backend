using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using plot_twist_back_end.Messages;

public class MessageQueue {
    private readonly ConcurrentQueue<string> _messageQueue = new();
    private bool _isProcessing = false;

    public async Task EnqueueMessage(string message, int socketId, BrushHandler bh, LinkHandler lh, WebSocketCoordinator wsc, BenchmarkHandler benchmarkHandler)
    {
        _messageQueue.Enqueue(message);

        if (!_isProcessing)
        {
            await StartProcessingQueue(socketId, bh, lh, wsc, benchmarkHandler);
        }
    }

    private async Task StartProcessingQueue(int socketId, BrushHandler bh, LinkHandler lh, WebSocketCoordinator wsc, BenchmarkHandler benchmarkHandler)
    {
        _isProcessing = true;
        while (_messageQueue.TryDequeue(out var message))
        {
            try {
                var clientMessage = JsonSerializer.Deserialize<Message>(message);
                HandleClientMessage(socketId, clientMessage, bh, lh, wsc, benchmarkHandler);
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"Invalid JSON format received: {ex.Message}");
            }
        }
        _isProcessing = false;
    }
    private static void HandleClientMessage(int socketId, Message clientMessage, BrushHandler bh, LinkHandler lh, WebSocketCoordinator wsc, BenchmarkHandler benchmarkHandler) {
        
        var serverResponse = new Message();
        switch (clientMessage.type) {
            case "link":
                LinkInfo linkInfo = clientMessage.links[0];
                Link link = new Link() {
                    Group = linkInfo.group,
                    Field = linkInfo.field,
                    DataSet = linkInfo.dataSet,
                };
                switch (clientMessage.links[0].action) {
                    case "create":
                        lh.CreateLinkGroup(link);
                        break;
                    case "delete":
                        lh.DeleteLinkGroup(link);
                        break;
                    case "update":
                        lh.UpdateFieldFromGroup(link);
                        break;
                }
                bh.updateClientsLinks(lh, wsc);
                bh.updateClientSelections(lh,wsc,0);
                break;
            case "selection":
                serverResponse.type = "selection";
                serverResponse.range = clientMessage.range;
                bh.updateSelection(socketId, clientMessage.range!, lh, wsc);
                bh.updateClientSelections(lh, wsc, socketId);
                break;
            case "addClient":
                wsc.InitializeClient(socketId);
                lh.AddDataset(clientMessage.dataSet?.name!);
                bh.AddClient(socketId, clientMessage.dataSet?.name!, clientMessage.dataSet?.fields!, wsc, lh);
                bh.updateClientSelections(lh, wsc, 0);
                bh.updateClientsLinks(lh, wsc);
                break;
            case "BenchMark":
                HandleBenchmarkAction(clientMessage, serverResponse, socketId, benchmarkHandler, wsc, lh, bh);
                break;
        }
    }
    
    private static void HandleBenchmarkAction(
    Message clientMessage,
    Message serverResponse,
    int socketId,
    BenchmarkHandler benchmarkHandler,
    WebSocketCoordinator wsc,
    LinkHandler lh,
    BrushHandler bh)
    {
        serverResponse.type = "BenchMark";

        switch (clientMessage.benchMark?.action)
        {
            case "addClientBenchMark":
                var clientInfo = clientMessage.benchMark?.clientInfo ?? throw new InvalidOperationException("clientInfo should not be null");
                int id = clientMessage.benchMark?.clientId ?? throw new InvalidOperationException("clientId should not be null");
                Console.WriteLine($"addClientBenchMark: Adding client {id} to bench mark");
                benchmarkHandler.AddClient(id, clientInfo);

                wsc.InitializeClient(socketId);
                lh.AddDataset(clientMessage.dataSet?.name!);
                bh.AddClient(socketId, clientMessage.dataSet?.name!, clientMessage.dataSet?.fields!, wsc, lh);
                break;

            case "start":
                Console.WriteLine("start: Sending benchmark start");
                serverResponse.benchMark = new BenchMark()
                {
                    action = "start",
                };
                bh.updateClientsLinks(lh, wsc);
                _ = wsc.BroadcastMessage(serverResponse, 0);
                break;
            
            case "doBrush":
                Console.WriteLine($"1_doBrush: sending do Brush from and to {clientMessage.benchMark?.clientId!}");
                serverResponse.benchMark = new BenchMark()
                {
                    action = "doBrushClient",
                    range = clientMessage.benchMark?.range!,
                    clientId = clientMessage.benchMark?.clientId ?? -1,
                    timeSent = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    brushId = clientMessage.benchMark?.brushId ?? -1,
                };
                _ = wsc.SendMessageToClient(serverResponse, socketId);
                break;

            case "brushed":
                Console.WriteLine($"2_brushed: The active client {clientMessage.benchMark?.clientId!} already brushed");
                {
                    var clientId = clientMessage.benchMark?.clientId ?? -1;
                    var timeToProcessBrushLocally = clientMessage.benchMark?.timeToProcessBrushLocally ?? -1;
                    var timeToUpdatePlots = clientMessage.benchMark?.timeToUpdatePlots ?? -1;
                    var timeSent = clientMessage.benchMark?.timeSent ?? -1;
                    var brushId = clientMessage.benchMark?.brushId ?? -1;
                    
                    var stopwatch = Stopwatch.StartNew();
                    bh.updateSelection(socketId, clientMessage.benchMark?.range!, lh, wsc);
                    bh.benchMarkUpdateClientSelections(lh, wsc, socketId, clientId, brushId); 
                    stopwatch.Stop();
                    var timeToProcess = stopwatch.Elapsed.TotalMilliseconds;
                    
                    var ping = (double)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - timeSent) - timeToUpdatePlots - timeToProcess;
                    benchmarkHandler.StoreSentBrushTimings(clientId, timeToProcessBrushLocally, timeToUpdatePlots, timeToProcess, ping); // TODO add brushId
                    benchmarkHandler.StorePing(clientId, ping, 1); 
                }
                break;

            case "selectionMade":
                {
                    // var clientId = clientMessage.benchMark?.clientId ?? -1;
                    // if (benchmarkHandler.isClientInitialized(clientId))
                    // {
                    //     var timeToProcessBrushLocally = clientMessage.benchMark?.timeToProcessBrushLocally ?? -1;
                    //     var timeToUpdatePlots = clientMessage.benchMark?.timeToUpdatePlots ?? -1;
                    //     var brushId = clientMessage.benchMark?.brushId ?? -1;
                    //
                    //     var stopwatch = Stopwatch.StartNew();
                    //     bh.updateSelection(socketId, clientMessage.benchMark?.range!, lh, wsc);
                    //     bh.benchMarkUpdateClientSelections(lh, wsc, socketId, clientId, brushId); 
                    //     stopwatch.Stop();
                    //     var timeToProcess = stopwatch.Elapsed.TotalMilliseconds;
                    //
                    //     // benchmarkHandler.StoreSentBrushTimings(clientId, timeToProcessBrushLocally, timeToUpdatePlots, timeToProcess, -1);
                    // }
                }
                break;

            case "receivedBrush":
                {
                    Console.WriteLine($"3_receivedBrush: The passive client {clientMessage.benchMark?.clientId!} brushed");
                    var clientId = clientMessage.benchMark?.clientId ?? -1;
                    var brushId = clientMessage.benchMark?.brushId ?? -1;
                    var brushClientId = clientMessage.benchMark?.brushClientId ?? -1;
                    var timeToProcessBrushLocally = clientMessage.benchMark?.timeToProcessBrushLocally ?? -1;
                    var timeToUpdatePlots = clientMessage.benchMark?.timeToUpdatePlots ?? -1;
                    var timeSent = clientMessage.benchMark?.timeSent ?? -1;
                    
                    var post = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var ping = post - timeSent - timeToProcessBrushLocally - timeToUpdatePlots;
                    benchmarkHandler.StoreReceivedBrushTimings(clientId, timeToProcessBrushLocally, timeToUpdatePlots, ping); // TODO: add brushId and brushClientId
                }
                break;

            case "end":
                benchmarkHandler.DownloadSummarizedData();
                benchmarkHandler.Reset();
                serverResponse.benchMark = new BenchMark()
                {
                    action = "end",
                };
                _ = wsc.BroadcastMessage(serverResponse, 0);
                break;
        }
    }
}
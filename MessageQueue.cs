using System.Collections.Concurrent;
using System.Text.Json;
using plot_twist_back_end;

public class MessageQueue {
    private readonly ConcurrentQueue<string> _messageQueue = new();
    private bool _isProcessing = false;

    public async Task EnqueueMessageAsync(string message, int socketId, BrushHandler bh, LinkHandler lh, WebSocketCoordinator wsc, BenchmarkHandler benchmarkHandler)
    {
        _messageQueue.Enqueue(message);
        if (!_isProcessing)
        {
            await StartProcessingQueueAsync(socketId, bh, lh, wsc, benchmarkHandler);
        }
    }
    private async Task StartProcessingQueueAsync(int socketId, BrushHandler bh, LinkHandler lh, WebSocketCoordinator wsc, BenchmarkHandler benchmarkHandler)
    {
        _isProcessing = true;
        while (_messageQueue.TryDequeue(out var message))
        {
            try {
                var clientMessage = JsonSerializer.Deserialize<plot_twist_back_end.Message>(message);
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
                bh.updateSelection(socketId, clientMessage.range, lh, wsc);
                break;
            case "addClient":
                wsc.InitializeClient(socketId);
                lh.AddDataset(clientMessage.dataSet?.name);
                bh.AddClient(socketId, clientMessage.dataSet?.name, clientMessage.dataSet?.fields, wsc, lh);
                bh.updateClientSelections(lh, wsc, 0);
                bh.updateClientsLinks(lh, wsc);
                break;
            case "BenchMark":
                serverResponse.type = "BenchMark";
                switch (clientMessage.benchMark?.action) {
                    case "addClientBenchMark":
                        var clientInfo = clientMessage.benchMark?.clientInfo ?? throw new InvalidOperationException("clientInfo should not be null");
                        int id = clientMessage.benchMark?.clientId ?? throw new InvalidOperationException("clientId should not be null");
                        benchmarkHandler.AddClient(id, clientInfo);
                        
                        wsc.InitializeClient(socketId);
                        lh.AddDataset(clientMessage.dataSet?.name);
                        bh.AddClient(socketId, clientMessage.dataSet?.name, clientMessage.dataSet?.fields, wsc, lh);
                        break;
                    case "start":
                        serverResponse.benchMark = new BenchMark() {
                            action = "start",
                        };
                        bh.updateClientsLinks(lh, wsc);
                        _ = wsc.BroadcastMessage(serverResponse, 0);
                        break;

                    case "brush": {
                        // Console.WriteLine($"Brush made: {JsonSerializer.Serialize(clientMessage.benchMark)}");
                        var clientId = clientMessage.benchMark?.clientId ?? -1;
                        var timeToProcessBrushLocally = clientMessage.benchMark?.timeToProcessBrushLocally ?? -1;
                        var timeToUpdatePlots = clientMessage.benchMark?.timeToUpdatePlots ?? -1;
                        var pre = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var ping = pre - (clientMessage.benchMark?.timeSent ?? -1);
                        bh.updateSelection(socketId, clientMessage.benchMark?.range, lh, wsc);
                        var timeToProcess = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) - pre;
                        // Console.WriteLine($"BrushMade time Sent: {clientMessage.benchMark?.timeSent}");
                        
                        benchmarkHandler.StoreSentBrushTimings(clientId, timeToProcessBrushLocally, timeToUpdatePlots, ping, timeToProcess);

                        serverResponse.type = "ping";
                        serverResponse.benchMark = new BenchMark() {
                            timeSent = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            pingType = 1,
                        };
                        _ = wsc.SendMessageToClient(serverResponse, socketId);
                        }
                        break;

                    case "receivedBrush": {
                        var clientId = clientMessage.benchMark?.clientId ?? -1;
                        var timeToProcessBrushLocally = clientMessage.benchMark?.timeToProcessBrushLocally ?? -1;
                        var timeToUpdatePlots = clientMessage.benchMark?.timeToUpdatePlots ?? -1;
                        var post = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var ping = post - (clientMessage.benchMark?.timeReceived ?? -1);
                        benchmarkHandler.StoreReceivedBrushTimings(clientId, timeToProcessBrushLocally, timeToUpdatePlots, ping);
                        
                        serverResponse.type = "ping";
                        serverResponse.benchMark = new BenchMark() {
                            timeSent = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            pingType = 0,
                        };
                        _ = wsc.SendMessageToClient(serverResponse, socketId);
                    }

                        break;
                    case "ping": {
                        // Console.WriteLine("PING");
                        // Console.WriteLine($"Brush received: {JsonSerializer.Serialize(clientMessage.benchMark)}");

                        var clientId = clientMessage.benchMark?.clientId ?? throw new Exception("Null");
                        var ping = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (clientMessage.benchMark?.timeSent ?? throw new Exception("Null")); 
                        var pingType = clientMessage.benchMark?.pingType ?? throw new Exception("Null");
                        benchmarkHandler.StorePing(clientId, ping, pingType);
                    }
                        break;
                    case "end":
                        benchmarkHandler.DownloadData();
                        benchmarkHandler.Reset();
                        serverResponse.benchMark = new BenchMark() {
                            action = "end",
                        };
                        _ = wsc.BroadcastMessage(serverResponse, 0);
                        bh.removeAllClients();
                        break;
                }
                break;
        }
    }
}
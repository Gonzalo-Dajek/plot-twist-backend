using System.Diagnostics;
using System.Text.Json;
using plot_twist_back_end.Messages;

public class MessageHandler {

    public void handleMessage(string message, int socketId, BrushHandler bh, LinkHandler lh, WebSocketHandler wsc, BenchmarkHandler benchmarkHandler)
    {
        var clientMessage = JsonSerializer.Deserialize<Message>(message);
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
                HandleBenchmarkAction(clientMessage, socketId, benchmarkHandler, wsc, lh, bh);
                break;
        }
    }
    
    private static void HandleBenchmarkAction(Message clientMessage, int socketId, BenchmarkHandler benchmarkHandler, WebSocketHandler wsc, LinkHandler lh, BrushHandler bh)
    {
        var serverResponse = new Message();
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
                serverResponse.benchMark = new BenchMarkMsg()
                {
                    action = "start",
                };
                bh.updateClientsLinks(lh, wsc);
                _ = wsc.BroadcastMessage(serverResponse, 0);
                break;
            
            case "end":
                benchmarkHandler.DownloadSummarizedData();
                benchmarkHandler.Reset();
                serverResponse.benchMark = new BenchMarkMsg()
                {
                    action = "end",
                };
                _ = wsc.BroadcastMessage(serverResponse, 0);
                break;

            case "processBrushInServer":
                {
                    var clientId = clientMessage.benchMark?.clientId ?? -1;
                    var brushId = clientMessage.benchMark?.brushId ?? -1;

                    var stopwatch = Stopwatch.StartNew();
                    bh.updateSelection(socketId, clientMessage.benchMark?.range!, lh, wsc);
                    bh.updateClientSelections(lh, wsc, socketId);
                    stopwatch.Stop();
                    
                    var timeToProcess = stopwatch.Elapsed.TotalMilliseconds;
                    
                    benchmarkHandler.RecordProcessBrushInServer(clientId, brushId, timeToProcess);
                }
                break;
            
            case "updateIndexes":
                {
                    string status = (clientMessage.benchMark?.isActiveBrush ?? false) ? "Sent" : "Received";
                    Console.WriteLine($"Index|ClientId:{clientMessage.benchMark?.clientId!}|BrushId:{clientMessage.benchMark?.brushId!}|{status}");
                    
                    var clientId = clientMessage.benchMark?.clientId ?? -1;
                    var brushId = clientMessage.benchMark?.brushId ?? -1;
                    var timeToProcessBrush = clientMessage.benchMark?.timeToProcessBrushLocally ?? -1;
                    var wasSentBrush = clientMessage.benchMark?.isActiveBrush;
                    
                    benchmarkHandler.RecordProcessBrush(clientId, brushId, timeToProcessBrush, wasSentBrush ?? true);
                }

                break;

            case "updatePlots":
                {
                    string status = (clientMessage.benchMark?.isActiveBrush ?? false)  ? "Sent" : "Received";
                    Console.WriteLine($"Plots|ClientId:{clientMessage.benchMark?.clientId!}|BrushId:{clientMessage.benchMark?.brushId!}|{status}");
                    
                    var clientId = clientMessage.benchMark?.clientId ?? -1;
                    var brushId = clientMessage.benchMark?.brushId ?? -1;
                    var timeToUpdatePlots = clientMessage.benchMark?.timeToUpdatePlots ?? -1;
                    var wasSentBrush = clientMessage.benchMark?.isActiveBrush;
                    

                    benchmarkHandler.RecordUpdatePlots(clientId, brushId, timeToUpdatePlots, wasSentBrush ?? true);
                }
                
                break;
        }
    }
}
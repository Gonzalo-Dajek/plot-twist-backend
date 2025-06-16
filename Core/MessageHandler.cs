using System.Diagnostics;
using System.Text.Json;
using plot_twist_back_end.Messages;

namespace plot_twist_back_end.Core;

public class MessageHandler
{
    public ClientsSelections selections { get; }
    public CrossDataSetLinks links { get; }
    public WebSocketCoordinator wsCoordinator{ get; }
    public Benchmark benchmark;
    
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

    public void HandleMessage(string message, int socketId)
    {
        var clientMessage = JsonSerializer.Deserialize<Message>(message);
        string jsonString = JsonSerializer.Serialize(clientMessage, new JsonSerializerOptions { WriteIndented = true });
        // Console.WriteLine(jsonString);
        // Console.WriteLine("--------------------------------------------------------------------");

        var serverResponse = new Message();
        switch (clientMessage.type) {
            case "link":
                links.UpdateClientsLinks(clientMessage.links!);
                links.broadcastClientsLinks();
                selections.ThrottledBroadcastClientsSelections(0);
                break;
            case "selection":
                serverResponse.type = "selection";
                serverResponse.clientsSelections = clientMessage.clientsSelections;
                selections.UpdateClientSelection(socketId, clientMessage.clientsSelections![0]);
                selections.ThrottledBroadcastClientsSelections(socketId);
                break;
            case "addClient":
                wsCoordinator.InitializeClient(socketId);
                selections.AddClient(socketId);
                selections.ThrottledBroadcastClientsSelections(0);
                links.broadcastClientsLinks();
                break;
            case "addDataSet":
                links.AddDataset(clientMessage.dataSet![0]);
                break;
            case "BenchMark":
                HandleBenchmarkAction(clientMessage, socketId, benchmark, wsCoordinator, links, selections);
                break;
        }
    }
    
    private static void HandleBenchmarkAction(Message clientMessage, int socketId, Benchmark benchmark, WebSocketCoordinator wsc, CrossDataSetLinks lh, ClientsSelections bh)
    {
        var serverResponse = new Message();
        serverResponse.type = "BenchMark";

        switch (clientMessage.benchMark?.action)
        {
            case "addClientBenchMark":
                var clientInfo = clientMessage.benchMark?.clientInfo ?? throw new InvalidOperationException("clientInfo should not be null");
                int id = clientMessage.benchMark?.clientId ?? throw new InvalidOperationException("clientId should not be null");
                Console.WriteLine($"addClientBenchMark: Adding client {id} to bench mark");
                benchmark.AddClient(id, clientInfo);

                wsc.InitializeClient(socketId);
                // TODO: lh.AddDataset(clientMessage.dataSet?.name!);
                bh.AddClient(socketId);
                break;

            case "start":
                Console.WriteLine("start: Sending benchmark start");
                serverResponse.benchMark = new BenchMarkMsg()
                {
                    action = "start",
                };
                // TODO: bh.updateClientsLinks(lh, wsc);
                _ = wsc.BroadcastMessage(serverResponse, 0);
                break;
            
            case "end":
                benchmark.DownloadSummarizedData();
                benchmark.Reset();
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
                    bh.UpdateClientSelection(socketId, clientMessage.benchMark?.range![0] ?? new ClientSelection());
                    bh.ThrottledBroadcastClientsSelections(socketId);
                    stopwatch.Stop();
                    
                    var timeToProcess = stopwatch.Elapsed.TotalMilliseconds;
                    
                    benchmark.RecordProcessBrushInServer(clientId, brushId, timeToProcess);
                }
                break;
            
            case "updateIndexes":
                {
                    // string status = (clientMessage.benchMark?.isActiveBrush ?? false) ? "Sent" : "Received";
                    // Console.WriteLine($"Index|ClientId:{clientMessage.benchMark?.clientId!}|BrushId:{clientMessage.benchMark?.brushId!}|{status}");
                    
                    var clientId = clientMessage.benchMark?.clientId ?? -1;
                    var brushId = clientMessage.benchMark?.brushId ?? -1;
                    var timeToProcessBrush = clientMessage.benchMark?.timeToProcessBrushLocally ?? -1;
                    var wasSentBrush = clientMessage.benchMark?.isActiveBrush;
                    
                    benchmark.RecordProcessBrush(clientId, brushId, timeToProcessBrush, wasSentBrush ?? true);
                }
                break;

            case "updatePlots":
                {
                    // string status = (clientMessage.benchMark?.isActiveBrush ?? false)  ? "Sent" : "Received";
                    // Console.WriteLine($"Plots|ClientId:{clientMessage.benchMark?.clientId!}|BrushId:{clientMessage.benchMark?.brushId!}|{status}");
                    
                    var clientId = clientMessage.benchMark?.clientId ?? -1;
                    var brushId = clientMessage.benchMark?.brushId ?? -1;
                    var timeToUpdatePlots = clientMessage.benchMark?.timeToUpdatePlots ?? -1;
                    var wasSentBrush = clientMessage.benchMark?.isActiveBrush;
                    

                    benchmark.RecordUpdatePlots(clientId, brushId, timeToUpdatePlots, wasSentBrush ?? true);
                }
                break;
        }
    }
}

using System.Collections.Concurrent;
using System.Text.Json;

public class MessageQueue {
    private readonly ConcurrentQueue<string> _messageQueue = new();
    private bool _isProcessing = false;

    public async Task EnqueueMessageAsync(string message, int socketId, BrushHandler bh, LinkHandler lh, WebSocketCoordinator wsc)
    {
        _messageQueue.Enqueue(message);
        if (!_isProcessing)
        {
            await StartProcessingQueueAsync(socketId, bh, lh, wsc);
        }
    }

    private async Task StartProcessingQueueAsync(int socketId, BrushHandler bh, LinkHandler lh, WebSocketCoordinator wsc)
    {
        _isProcessing = true;
        while (_messageQueue.TryDequeue(out var message))
        {
            try {
                // await Task.Delay(100);
                var clientMessage = JsonSerializer.Deserialize<plot_twist_back_end.Message>(message);
                handleClientMessage(socketId, clientMessage, bh, lh, wsc);
            }
            catch (JsonException ex)
            {
                Console.WriteLine("Invalid JSON format received");
            }
        }
        _isProcessing = false;
    } 
    private static void handleClientMessage(int socketId, plot_twist_back_end.Message clientMessage, BrushHandler bh, LinkHandler lh, WebSocketCoordinator wsc) {
        
        var serverResponse = new plot_twist_back_end.Message();
        switch (clientMessage.type) {
            case "link":
                plot_twist_back_end.LinkInfo linkInfo = clientMessage.links[0];
                Link link = new Link() {
                    DataSet1 = linkInfo.dataSet1,
                    DataSet2 = linkInfo.dataSet2,
                    Field1 = linkInfo.field1,
                    Field2 = linkInfo.field2,
                };
                switch (clientMessage.links[0].action) {
                    case "add":
                        lh.AddLink(link, linkInfo.timeOfCreation, wsc);
                        bh.updateClients(lh,wsc, 0);
                        break;
                    case "delete":
                        lh.RemoveLink(link, wsc);
                        bh.updateClients(lh,wsc,0);
                        break;
                    case "relink":
                        lh.Relink(link, wsc);
                        bh.updateClients(lh,wsc,0);
                        break;
                    case "unlink":
                        lh.Unlink(link, wsc);
                        bh.updateClients(lh,wsc,0);
                        break;
                }
                break;
            case "selection":
                serverResponse.type = "selection";
                serverResponse.range = clientMessage.range;
                bh.updateSelection(socketId, clientMessage.range, lh, wsc);
                break;
            case "addClient":
                bh.AddClient(socketId, clientMessage.dataSet?.name, clientMessage.dataSet?.fields, wsc, lh);
                break;
        }
    }
}
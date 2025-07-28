using System.Text;
using System.Text.Json; 
using System.Collections.Concurrent;
using System.Net.WebSockets;
using plot_twist_back_end.Messages;
namespace plot_twist_back_end.Core;
public class WebSocketCoordinator
{
    private ConcurrentDictionary<int, WebSocket> _webSockets = new ConcurrentDictionary<int, WebSocket>();
    private Dictionary<int, bool> _isInitilized = new Dictionary<int, bool>();
    private int _nextId = 1;

    public int AddWebSocket(WebSocket webSocket)
    {
        int id = _nextId++;
        _webSockets.TryAdd(id, webSocket);
        _isInitilized.TryAdd(id, false);
        return id;
    }

    public void RemoveWebSocket(int id)
    {
        _webSockets.TryRemove(id, out _);
        _isInitilized.Remove(id, out _);
    }

    public async Task BroadcastMessage(Message message, int socketId, bool ignoreNulls = true)
    {
        string jsonString = JsonSerializer.Serialize(message, new JsonSerializerOptions { WriteIndented = true });
        // Console.WriteLine($"Sent message from: {socketId}");
        // Console.WriteLine(jsonString);
        // Console.WriteLine("--------------------------------------------------------------------");

        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = ignoreNulls
                ? System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                : System.Text.Json.Serialization.JsonIgnoreCondition.Never
        };

        var jsonResponse = JsonSerializer.Serialize(message, options);
        var responseBytes = Encoding.UTF8.GetBytes(jsonResponse);
        
        foreach (var socketPair in _webSockets)
        {
            var socket = socketPair.Value;
            var id = socketPair.Key;
            if (!HasBeenInitialized(id) || id == socketId || socket.State != WebSocketState.Open)
                continue;

            try
            {
                await socket.SendAsync(new ArraySegment<byte>(responseBytes),
                    WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // client hung up; remove it
                _webSockets.TryRemove(id, out _);
                _isInitilized.Remove(id);
            }
        }
    }
    
    public async Task SendMessageToClient(Message message, int socketId, bool ignoreNulls = true)
    {
        if (_webSockets.TryGetValue(socketId, out var socket) && socket.State == WebSocketState.Open)
        {
            var options = new JsonSerializerOptions
            {
                DefaultIgnoreCondition = ignoreNulls
                    ? System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                    : System.Text.Json.Serialization.JsonIgnoreCondition.Never
            };
            var jsonResponse = JsonSerializer.Serialize(message, options);
            var responseBytes = Encoding.UTF8.GetBytes(jsonResponse);

            try
            {
                await socket.SendAsync(new ArraySegment<byte>(responseBytes),
                    WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (WebSocketException)
            {
                _webSockets.TryRemove(socketId, out _);
                _isInitilized.Remove(socketId);
            }
        }
    }


    // public async Task SendSelectionPerDataSet(Dictionary<string, rangeSet> selectionPerDataSet, Dictionary<int, string> dataSetPerClient, int socketId) {
    //     var options = new JsonSerializerOptions
    //     {
    //         DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    //     };       
    //     
    //     foreach (var socketPair in _webSockets)
    //     {
    //         var socket = socketPair.Value;
    //         var id = socketPair.Key;
    //         string dataSet = dataSetPerClient[id];
    //         plot_twist_back_end.RangeSelection[] rangeSelections = selectionPerDataSet[dataSet].ToArr();
    //
    //         plot_twist_back_end.Message m = new plot_twist_back_end.Message() {
    //             type="selection",
    //             range=rangeSelections,
    //         };
    //         
    //         var jsonResponse = JsonSerializer.Serialize(m, options);
    //         var responseBytes = Encoding.UTF8.GetBytes(jsonResponse);
    //         
    //         if (socket.State == WebSocketState.Open && id!=socketId)
    //         {
    //             await socket.SendAsync(new ArraySegment<byte>(responseBytes), WebSocketMessageType.Text, true, CancellationToken.None);
    //         }
    //     }
    // }
    
    // public async Task SendSelectionPerClient(Dictionary<int, rangeSet> selectionPerClient, int socketId) {
    //     // TODO: move to brush handler
    //     var options = new JsonSerializerOptions
    //     {
    //         DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    //     };       
    //     
    //     foreach (var socketPair in _webSockets)
    //     {
    //         var socket = socketPair.Value;
    //         var id = socketPair.Key;
    //         if (HasBeenInitialized(id)) {
    //             RangeSelection[] rangeSelections = selectionPerClient[id].ToArr();
    //
    //             Message m = new Message() {
    //                 type="selection",
    //                 range=rangeSelections,
    //             };
    //         
    //             var jsonResponse = JsonSerializer.Serialize(m, options);
    //             var responseBytes = Encoding.UTF8.GetBytes(jsonResponse);
    //         
    //             try
    //             {
    //                 await socket.SendAsync(new ArraySegment<byte>(responseBytes),
    //                     WebSocketMessageType.Text, true, CancellationToken.None);
    //             }
    //             catch (WebSocketException)
    //             {
    //                 _webSockets.TryRemove(id, out _);
    //                 _isInitilized.Remove(id);
    //             }
    //         }
    //     }
    // }

    // public async void UpdateLinksPerClient(Dictionary<int,LinkInfo[]> linkGroupsPerClient) {
    //     var options = new JsonSerializerOptions
    //     {
    //         DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    //     };       
    //     
    //     foreach (var socketPair in _webSockets)
    //     {
    //         var socket = socketPair.Value;
    //         var id = socketPair.Key;
    //
    //         if (HasBeenInitialized(id)) {
    //             Message m = new Message() {
    //                 type = "link",
    //                 links = linkGroupsPerClient[id],
    //             };
    //
    //             var jsonResponse = JsonSerializer.Serialize(m, options);
    //             var responseBytes = Encoding.UTF8.GetBytes(jsonResponse);
    //
    //             try
    //             {
    //                 await socket.SendAsync(new ArraySegment<byte>(responseBytes),
    //                     WebSocketMessageType.Text, true, CancellationToken.None);
    //             }
    //             catch (WebSocketException)
    //             {
    //                 _webSockets.TryRemove(id, out _);
    //                 _isInitilized.Remove(id);
    //             }
    //         }
    //     }
    // }

    private bool HasBeenInitialized(int id) {
        _isInitilized.TryGetValue(id, out var isInit);
        return isInit;
    }

    public void InitializeClient(int id) {
        _isInitilized[id] = true;
    }
}

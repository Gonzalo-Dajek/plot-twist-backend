using System.Net.WebSockets;
using System.Text;
using plot_twist_back_end.Core;

namespace plot_twist_back_end;
public static class PlotTwistBackEnd
{
    public static void Main(string[] args) {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(System.Net.IPAddress.Any, 5226);
        });

        // Enable CORS
        var services = builder.Services;
        services.AddCors(options =>
        {
            options.AddPolicy("AllowRequestFromAnyOrigin", policyBuilder =>
            {
                policyBuilder.AllowAnyOrigin() // Allow requests from a frontend served anywhere
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            });
        });

        var app = builder.Build();
        app.UseCors("AllowRequestFromAnyOrigin");
        app.UseHttpsRedirection();
        app.UseWebSockets();
        
        var wsCoordinator = new WebSocketCoordinator();
        var links = new CrossDataSetLinks(wsCoordinator);
        var selections = new ClientsSelections(wsCoordinator, links);
        var benchMark = new Benchmark();
        
        var messageHandler = new MessageHandler(selections, links, wsCoordinator, benchMark);
        // WebSocket request handling
        app.Use(async (context, next) =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                await HandleWebSocketCommunication(webSocket, messageHandler);
            }
            else
            {
                await next();
            }
        });

        app.Run();
    }

    private static async Task HandleWebSocketCommunication(WebSocket webSocket, MessageHandler messageHandler)
    {
        int socketId = messageHandler.wsCoordinator.AddWebSocket(webSocket);
        Console.WriteLine($"New WebSocket connection with id: {socketId}");

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                using var ms = new MemoryStream();
                var buffer = new byte[4 * 1024];

                try
                {
                    // Accumulate fragmented frames
                    do
                    {
                        result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                        if (result.CloseStatus.HasValue)
                        {
                            await webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
                            return;
                        }

                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);
                }
                catch (WebSocketException)
                {
                    // Client disconnected unexpectedly
                    break;
                }

                // Decode complete JSON text
                ms.Seek(0, SeekOrigin.Begin);
                string json = Encoding.UTF8.GetString(ms.ToArray());
                await messageHandler.HandleMessage(json, socketId);
            }
        }
        finally
        {
            try
            {
                if (webSocket.State != WebSocketState.Closed && webSocket.State != WebSocketState.Aborted)
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            catch (WebSocketException)
            {
                Console.WriteLine($"Connection {socketId} closed unexpectedly.");
            }
            finally
            {
                messageHandler.wsCoordinator.RemoveWebSocket(socketId);
                messageHandler.selections.RemoveClient(socketId);
                Console.WriteLine($"WebSocket with id {socketId} closed");
            }
        }

    }
}

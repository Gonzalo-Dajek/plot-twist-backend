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
        var selections = new ClientsSelections(wsCoordinator);
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

        var buffer = new byte[1024 * 512];

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                }
                catch (WebSocketException ex)
                {
                    Console.WriteLine($"1.DEBUG: Exception for socket {socketId}: {ex}");
                    break; // Exit loop on broken connection
                }

                if (result.CloseStatus.HasValue)
                    break;

                var receivedMessage = Encoding.UTF8.GetString(buffer, 0, result.Count);
                // Console.WriteLine($"Received msg from {socketId}");
                messageHandler.HandleMessage(receivedMessage, socketId);
            }
        }
        finally
        {
            if (webSocket.State != WebSocketState.Closed)
            {
                try
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
                catch (WebSocketException ex)
                {
                    Console.WriteLine($"2.DEBUG: Exception for socket {socketId}: {ex}");
                }
            }

            messageHandler.wsCoordinator.RemoveWebSocket(socketId);
            messageHandler.selections.RemoveClient(socketId);
            Console.WriteLine($"WebSocket with id {socketId} closed");
        }
    }
}

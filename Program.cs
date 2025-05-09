using System.Net.WebSockets;
using System.Text;

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
            options.AddPolicy("AllowLocalhost", policyBuilder =>
            {
                policyBuilder.AllowAnyOrigin() // Allow requests from a frontend served anywhere
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            });
        });

        var app = builder.Build();
        
        if (app.Environment.IsDevelopment())
        {
        }

        app.UseCors("AllowLocalhost");
        app.UseHttpsRedirection();
        app.UseWebSockets();
        
        var wsc = new WebSocketCoordinator();
        var lh = new LinkHandler();
        var bh = new BrushHandler();
        var mq = new MessageQueue();
        var benchMark = new BenchmarkHandler();

        // WebSocket request handling
        app.Use(async (context, next) =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                await HandleWebSocketCommunication(webSocket, wsc, lh, bh, mq, benchMark);
            }
            else
            {
                await next();
            }
        });

        app.Run();
    }

    private static async Task HandleWebSocketCommunication(
        WebSocket webSocket,
        WebSocketCoordinator wsc,
        LinkHandler lh,
        BrushHandler bh,
        MessageQueue mq,
        BenchmarkHandler benchmarkHandler)
    {
        // Add the WebSocket to the manager and get the assigned id
        int socketId = wsc.AddWebSocket(webSocket);
        Console.WriteLine($"New WebSocket connection with id: {socketId}");

        var buffer = new byte[1024 * 128];
        WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

        while (!result.CloseStatus.HasValue)
        {
            int currentQueueLength = mq.GetQueueLength();
            int maxQueueSize = mq.GetMaxQueueSize();
            if (currentQueueLength <= maxQueueSize)
            {
                var receivedMessage = Encoding.UTF8.GetString(buffer, 0, result.Count);
                // Console.WriteLine($"Received from {socketId}:");
                // Console.WriteLine(receivedMessage);

            
                await mq.EnqueueMessage(receivedMessage, socketId, bh, lh, wsc, benchmarkHandler);
            
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            }
            else
            {
                Console.WriteLine("Discarded message");
            }
        }

        // Remove the WebSocket from the manager when the connection closes
        await webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
        wsc.RemoveWebSocket(socketId);
        bh.removeClient(socketId, lh, wsc); 
        Console.WriteLine($"WebSocket with id {socketId} closed");
    }
}

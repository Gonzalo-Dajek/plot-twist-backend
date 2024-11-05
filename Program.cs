using System.Net.WebSockets;
using System.Text;
using System.Text.Json; 

public class plot_twist_back_end
{
    public static void Main(string[] args) {
        var builder = WebApplication.CreateBuilder(args);
        
        // Add services to the container.
        ConfigureServices(builder.Services);

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        Configure(app);

        app.Run();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();

        // Enable CORS
        services.AddCors(options =>
        {
            options.AddPolicy("AllowLocalhost", builder =>
            {
                builder.WithOrigins("http://localhost:5173") // Allow requests from your frontend
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            });
        });
    }

    private static void Configure(WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
            // Use the CORS policy
            app.UseCors("AllowLocalhost");
        }

        app.UseHttpsRedirection();
        


        // Define a simple GET endpoint that returns "Hello, World!"
        app.MapGet("/hello", () => new { m = "Hello, World!"})
            .WithName("GetHello")
            .WithOpenApi();
        
        // Enable WebSocket support
        app.UseWebSockets();
        
        WebSocketCoordinator wsc = new WebSocketCoordinator();
        LinkHandler lh = new LinkHandler();
        BrushHandler bh = new BrushHandler();
        MessageQueue mq = new MessageQueue();

        // WebSocket request handling
        app.Use(async (context, next) =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                await HandleWebSocketCommunication(webSocket, wsc, lh, bh, mq);
            }
            else
            {
                await next();
            }
        });
    }
    
    private static async Task HandleWebSocketCommunication(
        WebSocket webSocket,
        WebSocketCoordinator wsc,
        LinkHandler lh,
        BrushHandler bh,
        MessageQueue mq)
    {
        // Add the WebSocket to the manager and get the assigned id
        int socketId = wsc.AddWebSocket(webSocket);
        Console.WriteLine($"New WebSocket connection with id: {socketId}");

        var buffer = new byte[1024 * 128];
        WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

        while (!result.CloseStatus.HasValue)
        {
            var receivedMessage = Encoding.UTF8.GetString(buffer, 0, result.Count);
            Console.WriteLine($"Received from {socketId}:");
            Console.WriteLine(receivedMessage);

            await mq.EnqueueMessageAsync(receivedMessage, socketId, bh, lh, wsc);
            


            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        }

        // Remove the WebSocket from the manager when the connection closes
        await webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
        wsc.RemoveWebSocket(socketId);
        bh.removeClient(socketId, lh, wsc); 
        Console.WriteLine($"WebSocket with id {socketId} closed");
    }



    public struct Message
    {
        public string type { get; set; }
        public RangeSelection[]? range { get; set; }
        public DataSetInfo? dataSet { get; set; }
        public LinkInfo[]? links { get; set; }
        
    }
    public struct RangeSelection
    {
        public string field { get; set; }
        public string type { get; set; }
        public double[] range { get; set; } 
        public string[] categories { get; set; }
    }
    
    public struct DataSetInfo 
    {
        public string name { get; set; }
        public string[] fields { get; set; }
    }
    
    public struct LinkInfo
    {
        public string dataSet1 {get; set; }
        public string field1 { get; set; }
        public string dataSet2 { get; set; }
        public string field2 { get; set; }
        public long timeOfCreation { get; set; }
        public string action { get; set; }
        public bool state { get; set; }
    }
}

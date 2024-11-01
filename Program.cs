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

        // WebSocket request handling
        app.Use(async (context, next) =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                await HandleWebSocketCommunication(context, webSocket, wsc, lh, bh);
            }
            else
            {
                await next();
            }
        });
    }
    
    private static async Task HandleWebSocketCommunication(HttpContext context, WebSocket webSocket, WebSocketCoordinator wsc, LinkHandler lh, BrushHandler bh)
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

            try
            {
                var clientMessage = JsonSerializer.Deserialize<Message>(receivedMessage);
                var serverResponse = new Message();
                
                // await Task.Delay(100);
                switch (clientMessage.type) {
                    case "link":
                        LinkInfo linkInfo = clientMessage.links[0];
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

                        // if (clientMessage.range.Length >= 3) {
                        //     var output = lh.Translate(clientMessage.range[0], "athlete_events_500.csv", "athlete_events_1000.csv");
                        //     var output2 = lh.Translate(clientMessage.range[1], "athlete_events_500.csv", "athlete_events_1000.csv");
                        //     var output3 = lh.Translate(clientMessage.range[2], "athlete_events_500.csv", "athlete_events_1000.csv");
                        // }
                        // await wsc.BroadcastMessage(serverResponse, socketId);
                        bh.updateSelection(socketId, clientMessage.range, lh, wsc);
                        break;
                    case "addClient":
                        bh.AddClient(socketId, clientMessage.dataSet?.name, clientMessage.dataSet?.fields, wsc, lh);
                        break;
                }
                
                // await wsc.BroadcastMessage(serverResponse, 0); // TODO: remove
            }
            catch (JsonException ex)
            {
                Console.WriteLine("Invalid JSON format received");
            }

            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        }

        // Remove the WebSocket from the manager when the connection closes
        await webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
        wsc.RemoveWebSocket(socketId);
        // bh.removeClient(); // TODO:
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

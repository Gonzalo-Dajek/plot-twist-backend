using System.Diagnostics;
using plot_twist_back_end.Messages;
namespace plot_twist_back_end.Core;

public class ClientsSelections {
    private Dictionary<int, ClientSelection> _selectionsPerClients = new Dictionary<int, ClientSelection>();
    private WebSocketCoordinator _wsCoordinator;
    
    private long _lastUpdateTimestamp = 0;
    private int? _pendingSocketId = null;
    private System.Timers.Timer _throttleTimer;
    private const int ThrottleIntervalMs = 70; 
    
    public ClientsSelections(WebSocketCoordinator wsCoordinator) {
        this._wsCoordinator = wsCoordinator;
    }
    
    public void AddClient(int socketId) {
        _selectionsPerClients[socketId] = new ClientSelection();
        BroadcastClientsSelections(socketId);
    }

    public void RemoveClient(int socketId) {
        _selectionsPerClients.Remove(socketId);
        BroadcastClientsSelections(0);
    }

    public void UpdateClientSelection(int socketId, ClientSelection socketSelection) {
        if (socketId != 0) {
            this._selectionsPerClients[socketId] = socketSelection;
        }
    }
    
    public void ThrottledBroadcastClientsSelections(int socketId)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedMs = (now - _lastUpdateTimestamp) * 1000 / Stopwatch.Frequency;

        if (elapsedMs >= ThrottleIntervalMs)
        {
            _lastUpdateTimestamp = Stopwatch.GetTimestamp();
            BroadcastClientsSelections(socketId);
        }
        else
        {
            _pendingSocketId = socketId;

            if (_throttleTimer == null)
            {
                _throttleTimer = new System.Timers.Timer();
                _throttleTimer.Elapsed += (_, __) =>
                {
                    _throttleTimer.Stop();
                    if (_pendingSocketId.HasValue)
                    {
                        _lastUpdateTimestamp = Stopwatch.GetTimestamp();
                        BroadcastClientsSelections(_pendingSocketId.Value);
                        _pendingSocketId = null;
                    }
                };
                _throttleTimer.AutoReset = false;
            }

            _throttleTimer.Interval = ThrottleIntervalMs - elapsedMs;
            _throttleTimer.Stop(); // reset if already running
            _throttleTimer.Start();
        }
    }
    
    public async void BroadcastClientsSelections(int socketId)
    {
        foreach (var (id,_) in _selectionsPerClients)
        {
            if(id == socketId) continue;
            Message msg = new Message();
            List<ClientSelection> clientSelections = new List<ClientSelection>();
            
            foreach (var (id2,selection) in _selectionsPerClients)
            {
                if (id == id2) continue; 
                clientSelections.Add(selection);
            }
            
            msg.type = "selection";
            msg.clientsSelections = clientSelections.ToArray();
            await _wsCoordinator.SendMessageToClient(msg, id);
        }
    }
}
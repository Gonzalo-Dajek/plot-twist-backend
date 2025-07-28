using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using plot_twist_back_end.Messages;
using plot_twist_back_end.Utils.Utils;

namespace plot_twist_back_end.Core;

public class ClientsSelections {
    private Dictionary<int, ClientSelection> _selectionsPerClients = new Dictionary<int, ClientSelection>();
    private WebSocketCoordinator _wsCoordinator;
    
    private long _lastUpdateTimestamp = 0;
    private int? _pendingSocketId = null;
    private System.Timers.Timer _throttleTimer;
    private const int ThrottleIntervalMs = 70; 
    private CrossDataSetLinks _links;
    private bool _isUpdating = false;

    public ClientsSelections(WebSocketCoordinator wsCoordinator, CrossDataSetLinks links) {
        this._wsCoordinator = wsCoordinator;
        this._links = links;
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

    public async void updateCrossDataSetSelectionLimited()
    {
        if (_isUpdating)
        {   
            Console.WriteLine("AAAAAAAAAAAAAAAAA");
            return;           // already working → drop this call
        }

        _isUpdating = true;

        await Task.Run(() => {
            var swUpdate = Stopwatch.StartNew();
            try
            {
                selectionSet clientSelections = new selectionSet();
                foreach (var (_, selection) in _selectionsPerClients)
                {
                    clientSelections.AddSelectionArr(selection.selectionPerDataSet);
                }
                var selectionPerDataset = clientSelections.ToArr();
                foreach (var selection in selectionPerDataset!)
                {
                    _links.updateDataSetSelection(selection.dataSetName!, selection.indexesSelected!.ToList());
                }
            
                _links.updateCrossDataSetSelection();
            
                var crossSelections = _links.getCrossSelections();

                foreach (var (id, _) in _selectionsPerClients)
                {
                    Message msg = new Message();
                    msg.type = "crossSelection";
                    msg.dataSetCrossSelection = crossSelections.ToArray();
                    _wsCoordinator.SendMessageToClient(msg, id, false).Wait();
                }
            }
            finally
            {
                _isUpdating = false;
            }
            swUpdate.Stop();
            Console.WriteLine($"took {swUpdate.ElapsedMilliseconds} ms"); 
        });
    }
    
    
    
    public async void BroadcastClientsSelections(int socketId)
    {
        selectionSet clientSelections = new selectionSet();
        foreach (var (_, selection) in _selectionsPerClients)
        {
            clientSelections.AddSelectionArr(selection.selectionPerDataSet);
        }
        // var swSqlite = Stopwatch.StartNew();
        // swSqlite.Stop();
        // Console.WriteLine($"UpdateSelectionsFromTable took {swSqlite.ElapsedMilliseconds} ms");
        
        // TODO: HEREEEEEEEEEEEEEEEEEEEEEEEEEEEEEE ?? benchmark were performance goes
        updateCrossDataSetSelectionLimited();
    
        // var crossSelections = _links.getCrossSelections();
        foreach (var (id, _) in _selectionsPerClients)
        {
            if (id == socketId) continue;
            Message msg = new Message();
            clientSelections = new selectionSet();
    
            foreach (var (id2, selection) in _selectionsPerClients)
            {
                if (id == id2) continue;
                clientSelections.AddSelectionArr(selection.selectionPerDataSet);
            }
    
            msg.type = "selection";
            ClientSelection clientSelection = new ClientSelection();
            clientSelection.selectionPerDataSet = clientSelections.ToArr();
            msg.clientsSelections = [clientSelection];
            
            
            await _wsCoordinator.SendMessageToClient(msg, id);
            
            // msg = new Message();
            // msg.type = "crossSelection";
            // msg.dataSetCrossSelection = crossSelections.ToArray();
            // await _wsCoordinator.SendMessageToClient(msg, id, false);
            
        }
    }
}
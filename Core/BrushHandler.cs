// namespace plot_twist_back_end;
using System.Diagnostics;

using plot_twist_back_end.Messages;

public class BrushHandler {
    private readonly SemaphoreSlim _throttleLock = new SemaphoreSlim(1, 1);
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private const int ThrottleIntervalMs = 100;
    private Dictionary<int,string> _clients = new Dictionary<int, string>();
    private Dictionary<string, string[]> _fields = new Dictionary<string, string[]>();
    private Dictionary<int, RangeSelection[]> _selectionsClients =
        new Dictionary<int, RangeSelection[]>();

    public async void AddClient(int socketId, string dataSet, string[] fields, WebSocketHandler wsc, LinkHandler lh) 
    {
        _clients[socketId] = dataSet;
        _fields[dataSet] = fields; 
        _selectionsClients[socketId] = new RangeSelection[] { }; 
    }

    public void removeClient(int socketId, LinkHandler lh, WebSocketHandler wsc) {
        this._clients.Remove(socketId);
        this._selectionsClients.Remove(socketId);
        this.updateClientSelections(lh, wsc, 0);
    }

    public async void updateSelection(int socketId, RangeSelection[] socketSelection, LinkHandler lh, WebSocketHandler wsc) {
        if (socketId != 0) {
            this._selectionsClients[socketId] = socketSelection;
        }
    }

    public async void updateClientsLinks(LinkHandler lh, WebSocketHandler wsc) {
        Dictionary<string, LinkInfo[]> linkGroupPerDataSet =
            new Dictionary<string, LinkInfo[]>();
        
        foreach (var (dataset,_) in this._fields) {
            linkGroupPerDataSet.Add(dataset, lh.ArrayOfLinks(dataset));
        }

        Dictionary<int, LinkInfo[]> linkGroupPerClient =
            new Dictionary<int, LinkInfo[]>();
        foreach (var (id, dataset) in this._clients) {
            linkGroupPerClient.Add(id, linkGroupPerDataSet[dataset]);
        }

        wsc.UpdateLinksPerClient(linkGroupPerClient);
    }
    
    public async Task throttledUpdateClientSelections(LinkHandler lh, WebSocketHandler wsc, int socketId)
    {
        await _throttleLock.WaitAsync();
        try
        {
            var elapsed = _stopwatch.ElapsedMilliseconds;
            if (elapsed < ThrottleIntervalMs)
                await Task.Delay(ThrottleIntervalMs - (int)elapsed);

            _stopwatch.Restart();
            updateClientSelections(lh, wsc, socketId);
        }
        finally
        {
            _throttleLock.Release();
        }
    }

    public async void updateClientSelections(LinkHandler lh, WebSocketHandler wsc, int socketId) {
        Dictionary<int, rangeSet> interesectionSelection = new Dictionary<int, rangeSet>(){};
        foreach (var id in this._clients.Keys) {
            interesectionSelection.Add(id, new rangeSet());
        }
        
        foreach (var (id, selection) in this._selectionsClients) {
            string dataSet1 = this._clients[id];
            foreach (var (id2, selection2) in this._selectionsClients) {
                string dataSet2 = this._clients[id2];

                if (id != id2) {
                    foreach (var rangeSelection in selection) { 
                        var selectionWithLinks = lh.Translate(rangeSelection, dataSet1, dataSet2);
                        if (selectionWithLinks != null) {
                            interesectionSelection[id2].AddSelection(selectionWithLinks.Value);
                        }
                    }                   
                }
            }
        }
        
        await wsc.SendSelectionPerClient(interesectionSelection, socketId);       
    }

    public void removeAllClients() {
        this._clients.Clear();
        this._fields.Clear();
        this._selectionsClients.Clear();
    }
}
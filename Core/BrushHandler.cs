// namespace plot_twist_back_end;


using plot_twist_back_end.Messages;

public class BrushHandler {
    private Dictionary<int,string> _clients = new Dictionary<int, string>();
    private Dictionary<string, string[]> _fields = new Dictionary<string, string[]>();
    private Dictionary<int, RangeSelection[]> _selectionsClients =
        new Dictionary<int, RangeSelection[]>();

    public async void AddClient(int socketId, string dataSet, string[] fields, WebSocketCoordinator wsc, LinkHandler lh) 
    {

        _clients[socketId] = dataSet; // Overwrite or add new
        _fields[dataSet] = fields; // Overwrite or add new
        _selectionsClients[socketId] = new RangeSelection[] { }; // Overwrite or add new

        // if (!_clients.ContainsKey(socketId)) 
        // {
        //     _clients.TryAdd(socketId, dataSet);
        //     _fields.TryAdd(dataSet, fields);
        //     _selectionsClients.TryAdd(socketId, new plot_twist_back_end.RangeSelection[] { });
        // }

        //
        // this._clients.Add(socketId, dataSet);
        // this._fields.TryAdd(dataSet, fields);
        // this._selectionsClients.Add(socketId,new plot_twist_back_end.RangeSelection[]{});
    }

    public void removeClient(int socketId, LinkHandler lh, WebSocketCoordinator wsc) {
        this._clients.Remove(socketId);
        this._selectionsClients.Remove(socketId);
        this.updateClientSelections(lh, wsc, 0);
    }

    public async void updateSelection(int socketId, RangeSelection[] socketSelection, LinkHandler lh, WebSocketCoordinator wsc) {
        if (socketId != 0) {
            this._selectionsClients[socketId] = socketSelection;
        }
        this.updateClientSelections(lh, wsc, socketId); // TODO: this.updateClientSelections(lh, wsc, 0);
    }

    public async void updateClientsLinks(LinkHandler lh, WebSocketCoordinator wsc) {
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

    public async void updateClientSelections(LinkHandler lh, WebSocketCoordinator wsc, int socketId) {
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
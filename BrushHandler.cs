// namespace plot_twist_back_end;
    

public class BrushHandler {
    private Dictionary<int,string> _clients = new Dictionary<int, string>();
    private Dictionary<string, string[]> _fields = new Dictionary<string, string[]>();
    private Dictionary<int, plot_twist_back_end.RangeSelection[]> _selectionsClients =
        new Dictionary<int, plot_twist_back_end.RangeSelection[]>();

    public async void AddClient(int socketId, string dataSet, string[] fields, WebSocketCoordinator wsc, LinkHandler lh) 
    {
        this._clients.Add(socketId, dataSet);
        this._fields.TryAdd(dataSet, fields);
        this._selectionsClients.Add(socketId,new plot_twist_back_end.RangeSelection[]{});

        this.updateClientSelections(lh, wsc, 0);
    }

    public void removeClient(int socketId, LinkHandler lh, WebSocketCoordinator wsc) {
        this._clients.Remove(socketId);
        this._selectionsClients.Remove(socketId);
        this.updateClientSelections(lh, wsc, 0);
    }

    public async void updateSelection(int socketId, plot_twist_back_end.RangeSelection[] socketSelection, LinkHandler lh, WebSocketCoordinator wsc) {
        if (socketId != 0) {
            this._selectionsClients[socketId] = socketSelection;
        }
        this.updateClientSelections(lh, wsc, 0);
    }

    public async void updateClientsLinks(LinkHandler lh, WebSocketCoordinator wsc) {
        Dictionary<string, plot_twist_back_end.LinkInfo[]> linkGroupPerDataSet =
            new Dictionary<string, plot_twist_back_end.LinkInfo[]>();
        
        foreach (var (dataset,_) in this._fields) {
            linkGroupPerDataSet.Add(dataset, lh.ArrayOfLinks(dataset));
        }

        Dictionary<int, plot_twist_back_end.LinkInfo[]> linkGroupPerClient =
            new Dictionary<int, plot_twist_back_end.LinkInfo[]>();
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
}
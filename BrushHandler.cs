
public class BrushHandler {
    private Dictionary<int,string> _clients = new Dictionary<int, string>();
    private Dictionary<string, string[]> _fields = new Dictionary<string, string[]>();
    private Dictionary<int, plot_twist_back_end.RangeSelection[]> _selectionsClients =
        new Dictionary<int, plot_twist_back_end.RangeSelection[]>();
    private Dictionary<string, plot_twist_back_end.RangeSelection[]> _selectionsDataSets =
        new Dictionary<string, plot_twist_back_end.RangeSelection[]>();

    public async void AddClient(int socketId, string dataSet, string[] fields, WebSocketCoordinator wsc, LinkHandler lh) 
    {
        this._clients.Add(socketId, dataSet);
        this._fields.TryAdd(dataSet, fields);
        this._selectionsClients.Add(socketId,new plot_twist_back_end.RangeSelection[]{});
        this._selectionsDataSets.TryAdd(dataSet, new plot_twist_back_end.RangeSelection[]{});
        var m = new plot_twist_back_end.Message() {
            type = "addClient",
            dataSet = new plot_twist_back_end.DataSetInfo() {
                name = dataSet,
                fields = fields,
            },
        };
        await wsc.BroadcastMessage(m, 0);
        this.updateClients(lh, wsc, 0);
    }

    public void removeClient(int socketId) 
    {
        // TODO:
    }

    public async void updateSelection(int socketId, plot_twist_back_end.RangeSelection[] socketSelection, LinkHandler lh, WebSocketCoordinator wsc) {
        if (socketId != 0) {
            this._selectionsClients[socketId] = socketSelection;
        }
        this.updateClients(lh, wsc, 0);
    }

    public async void updateClients(LinkHandler lh, WebSocketCoordinator wsc, int socketId) {
        Dictionary<string, rangeSet> interesectionSelection = new Dictionary<string, rangeSet>(){};
        foreach (var dataSets in this._fields.Keys) {
            interesectionSelection.Add(dataSets, new rangeSet());
        }
        
        foreach (var (id, selection) in this._selectionsClients) {
            string dataSet1 = this._clients[id];
            foreach (var dataSet2 in this._fields.Keys) {
                foreach (var rangeSelection in selection) {
                    var selectionWithLinks = lh.Translate(rangeSelection, dataSet1, dataSet2);
                    interesectionSelection[dataSet2].AddSelectionArr(selectionWithLinks);
                }
            }
        }
        
        await wsc.SendSelectionPerDataSet(interesectionSelection, this._clients, socketId);       
    }
}
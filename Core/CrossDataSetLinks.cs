
using plot_twist_back_end.Messages;

namespace plot_twist_back_end.Core;
public class CrossDataSetLinks {
    private WebSocketCoordinator _wsCoordinator;
    private List<DataSetInfo> _dataSets = new List<DataSetInfo>();
    private HashSet<string> _datasetNames = new HashSet<string>();
    private Link[] links;
    private int _dataSetId = -1;
    
    public CrossDataSetLinks(WebSocketCoordinator wsCoordinator) {
        _wsCoordinator = wsCoordinator;   
    }
    
    public int newDataSetId()
    {
        _dataSetId++;
        return this._dataSetId;
    }
    
    public void AddDataset(DataSetInfo dataset) {
        if (_datasetNames.Contains(dataset.name)) {
            return;
        }

        dataset.dataSetColorIndex = this.newDataSetId();
        _dataSets.Add(dataset);
        _datasetNames.Add(dataset.name);
    }

    public void UpdateClientsLinks(Link[] updatedLinks) {
        this.links = updatedLinks;
    }

    public async void broadcastClientsLinks() {
        Message msg = new Message();
        msg.type = "link";
        msg.dataSet = this._dataSets.ToArray();
        msg.links = this.links;
        await _wsCoordinator.BroadcastMessage(msg, 0);
    }
}
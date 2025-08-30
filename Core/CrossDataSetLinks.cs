
using System.Diagnostics;
using System.Runtime.CompilerServices;
using plot_twist_back_end.Messages;

namespace plot_twist_back_end.Core;

public struct DataSetSelection
{
    public string DataSet { get; set; }
    public List<List<string>> SelectedBy { get; set; }
}

public struct DataSetSelectionById
{
    public string DataSet { get; set; }
    public List<List<int>> SelectedByIds { get; set; }
}

public class CrossDataSetLinks {
    private WebSocketCoordinator _wsCoordinator;
    private List<DataSetInfo> _dataSets = new List<DataSetInfo>();
    private HashSet<string> _datasetNames = new HashSet<string>();
    private Link[] links = [];
    private int _dataSetId = -1;
    private string linkOperator = "And";
    private CrossDataSetSelections crossDataSetSelections;
    
    private bool _isUpdating = false;
    
    // DataSetTo -> (DataSetFrom -> indexSelected)
    private Dictionary<string, Dictionary<string, List<bool>>> _crossDataSetSelections =
        new Dictionary<string, Dictionary<string, List<bool>>>();

    public CrossDataSetLinks(WebSocketCoordinator wsCoordinator) {
        _wsCoordinator = wsCoordinator;
        crossDataSetSelections = new CrossDataSetSelections();
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
        crossDataSetSelections.AddDataset(dataset);
    }

    public void updateDataSetSelection(string datasetName, List<bool> selected)
    {
        crossDataSetSelections.UpdateSelectionsFromTable(datasetName, selected.ToList());
    }
    

    public List<DataSetSelectionById> getCrossSelections()
    {
        var dataSets = _dataSets;
        var selections = this.BuildCrossSelections();
        // build a map from dataset name to its color index (used here as ID)
        var idMap = dataSets
            .Where(ds => ds.dataSetColorIndex.HasValue)
            .ToDictionary(ds => ds.name, ds => ds.dataSetColorIndex!.Value);

        var result = new List<DataSetSelectionById>(selections.Count);

        foreach (var sel in selections)
        {
            var byIds = new List<List<int>>(sel.SelectedBy.Count);

            foreach (var fromList in sel.SelectedBy)
            {
                var idList = new List<int>(fromList.Count);

                foreach (var fromName in fromList)
                {
                    if (!idMap.TryGetValue(fromName, out var id))
                        throw new KeyNotFoundException(
                            $"Dataset name '{fromName}' not found or has no ID.");

                    idList.Add(id);
                }

                byIds.Add(idList);
            }

            result.Add(new DataSetSelectionById
            {
                DataSet        = sel.DataSet,
                SelectedByIds  = byIds
            });
        }

        return result;
    }

    public List<DataSetSelection> BuildCrossSelections()
    {
        var result = new List<DataSetSelection>(_crossDataSetSelections.Count);

        foreach (var kvp in _crossDataSetSelections)
        {
            string toName   = kvp.Key;
            var fromDict    = kvp.Value;

            // determine length (all inner lists are same size)
            int length = fromDict.Count > 0 
                ? fromDict.Values.First().Count 
                : 0;

            var selectedBy = new List<List<string>>(length);

            for (int i = 0; i < length; i++)
            {
                var selList = new List<string>();
                foreach (var fromKvp in fromDict)
                {
                    if (fromKvp.Value[i])
                        selList.Add(fromKvp.Key);
                }
                selectedBy.Add(selList);
            }

            result.Add(new DataSetSelection
            {
                DataSet    = toName,
                SelectedBy = selectedBy
            });
        }

        return result;
    }
    
    public void updateCrossDataSetSelection()
    {
        // clean entriesSelected
        Dictionary<string, Dictionary<string, List<bool>>> entrySelectedByPerDataSet = new();
        foreach (var dataSetA in _dataSets)
        {
            var innerDict = new Dictionary<string, List<bool>>();
            foreach (var dataSetB in _dataSets)
            {
                if(dataSetA.name==dataSetB.name) continue;
                // var boolArray = Enumerable.Repeat(false, dataSetA.length).ToList(); 
                var boolArray = new List<bool>(); 
                innerDict[dataSetB.name] = boolArray;
            }
            entrySelectedByPerDataSet[dataSetA.name] = innerDict;
        }
        this._crossDataSetSelections = entrySelectedByPerDataSet;
        
        // update all cross selections by link
        for (int i = 0; i < links.Length; i++)
        {
            ref Link link = ref links[i];
            string? from = link.state.dataSet1;
            string? to = link.state.dataSet2;
            if (from == to || from == null || to == null || from == "" || to == "")
            {
                link.isError = true;
                continue;
            }

            string predicate = link.state.inputField!;
            List<bool> previousSelected;
            if (link.type == "Direct Link")
            {
                List<bool> newSelected;
                previousSelected = entrySelectedByPerDataSet[to][from];
                if (crossDataSetSelections.TryEvaluateMatches(from, to, predicate, out newSelected))
                {
                    entrySelectedByPerDataSet[to][from] = combineBoolLists(previousSelected, newSelected, linkOperator);
                    link.isError = false;
                }
                else
                {
                    link.isError = true;
                }
            }
            else
            {
                List<bool> newSelectedFrom;
                List<bool> newSelectedTo;
                if (crossDataSetSelections.TryEvaluateMatchesBidirectional(from, to, predicate, out newSelectedTo, out newSelectedFrom))
                {
                    List <bool> previousSelectedTo = entrySelectedByPerDataSet[to][from];
                    List <bool> previousSelectedFrom = entrySelectedByPerDataSet[from][to];
                    entrySelectedByPerDataSet[to][from] = combineBoolLists(previousSelectedTo, newSelectedTo, linkOperator);
                    entrySelectedByPerDataSet[from][to] = combineBoolLists(previousSelectedFrom, newSelectedFrom, linkOperator);

                    link.isError = false;
                }
                else
                {
                    link.isError = true;
                }
            }
        }
        
        // if a list remains empty then fill it with all false(s)
        for (int i = 0; i < _dataSets.Count; i++)
        {
            var dataSetA = _dataSets[i];
            var innerDict = entrySelectedByPerDataSet[dataSetA.name];

            for (int j = 0; j < _dataSets.Count; j++)
            {
                var dataSetB = _dataSets[j];
                if (dataSetA.name == dataSetB.name) continue;
                if (innerDict[dataSetB.name].Count != 0) continue;

                var boolArray = Enumerable.Repeat(false, dataSetA.length).ToList();
                innerDict[dataSetB.name] = boolArray;
            }
        }
        
        this._crossDataSetSelections = entrySelectedByPerDataSet;
    }
    
    private static List<bool> combineBoolLists(List<bool> list1, List<bool> list2, string operation)
    {
        if (list1.Count == 0)
            return new List<bool>(list2);
        
        if (list1.Count != list2.Count)
            throw new ArgumentException("Lists must be of the same size.");

        return operation switch
        {
            "And" => list1.Zip(list2, (a, b) => a && b).ToList(),
            "Or"  => list1.Zip(list2, (a, b) => a || b).ToList(),
            _     => throw new ArgumentException("Operation must be 'And' or 'Or'.")
        };
    }

    public void UpdateClientsLinks(Link[] updatedLinks, string newLinkOperator) {
        this.links = updatedLinks;
        this.linkOperator = newLinkOperator;
    }

    public async void broadcastClientsLinks(int clientId = 0) {
        Message msg = new Message();
        msg.type = "link";
        msg.dataSet = this._dataSets.ToArray();
        msg.links = this.links;
        msg.linksOperator = this.linkOperator;
        await _wsCoordinator.BroadcastMessage(msg, clientId, false);
        
        msg.type = "linkUpdate";
        await _wsCoordinator.SendMessageToClient(msg, clientId);
    }
}
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices.JavaScript;
using plot_twist_back_end.Messages;

public struct Link{
    public string DataSet { get; set; }
    public string Group { get; set; }
    public string? Field { get; set; }
}

public class LinkHandler {
    //                Group               DataSet  Field
    private Dictionary<string, Dictionary<string, string?>> _linkGroups = new Dictionary<string, Dictionary<string, string?>>();
    private HashSet<string> _dataSets = new HashSet<string>();

    public void AddDataset(string dataSet) {
        this._dataSets.Add(dataSet);
        foreach (var (group, DataSetToField) in this._linkGroups) {
            DataSetToField.TryAdd(dataSet, null);
        }
    }
    
    public void CreateLinkGroup(Link l) {
        if (!this._linkGroups.ContainsKey(l.Group)) {
            this._linkGroups.Add(l.Group,new Dictionary<string, string?>());
            foreach (var dataSet in this._dataSets) {
                this._linkGroups[l.Group].Add(dataSet, null);
            }
        }
    }

    public void DeleteLinkGroup(Link l) 
    {
        if (this._linkGroups.ContainsKey(l.Group)) {
            this._linkGroups.Remove(l.Group);
        }
    }

    public void UpdateFieldFromGroup(Link l) {
        bool isNotAlreadyInAGroup = true;
        foreach (var dataSetToField in _linkGroups.Values) {
            if (dataSetToField.ContainsKey(l.DataSet)) {
                if (dataSetToField[l.DataSet] == l.Field && l.Field!=null) {
                    isNotAlreadyInAGroup = false;
                }
            }
        }
        if (this._linkGroups.ContainsKey(l.Group) && isNotAlreadyInAGroup) {
            if (this._linkGroups[l.Group].ContainsKey(l.DataSet)) {
                this._linkGroups[l.Group][l.DataSet] = l.Field;
            }
        }
    }

    public LinkInfo[] ArrayOfLinks(string dataSet) {
        var list = new List<LinkInfo>();
        foreach (var (group, dataSetToField) in this._linkGroups) {
            string? field = null;
            if (dataSetToField.TryGetValue(dataSet, out var value)) {
                field = value;
            }
            LinkInfo linkInfo = new LinkInfo() {
                action = "none",
                group = group,
                dataSet = dataSet,
                field = field,
            };
            list.Add(linkInfo);
        }
        return list.ToArray();
    }

    public RangeSelection? Translate(RangeSelection r, String ds1, String ds2) 
    {
        List<RangeSelection> ret = new List<RangeSelection>();
        if (ds1==ds2) {
            return r;
        }
        foreach (var (group, dataSetToField) in this._linkGroups) 
        {
            // if(matches fields/dataset inside same group) -> return other field
            bool isNumerical = r.type == "numerical";
            if (dataSetToField.ContainsKey(ds1) && dataSetToField.ContainsKey(ds2)) {
                if (dataSetToField[ds1] == r.field && dataSetToField[ds2]!=null) {
                    if (isNumerical) {
                        return new RangeSelection() {
                            field = dataSetToField[ds2], 
                            type = r.type, 
                            range = r.range, 
                        };                       
                    }
                    else {
                        return new RangeSelection() {
                            field = dataSetToField[ds2], 
                            type = r.type, 
                            categories = r.categories, 
                        };                                              
                    }
                }
            }
        }
        return null;
    }
}
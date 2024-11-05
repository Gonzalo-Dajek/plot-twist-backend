using System.Runtime.InteropServices.JavaScript;

public struct Link{
    public string DataSet1 { get; set; }
    public string Field1 { get; set; }
    public string DataSet2 { get; set; }
    public string Field2 { get; set; }
}
public class LinkHandler {
    private Dictionary<Link,(long, bool)> _stateOfLinks = new Dictionary<Link, (long, bool)>();

    private Link inverse(Link l) {
        Link linv = new Link() {
            DataSet1 = l.DataSet2,
            Field1 = l.Field2,
            DataSet2 = l.DataSet1,
            Field2 = l.Field1,
        };
        return linv;
    }

    private async void UpdateClientLinks(WebSocketCoordinator wsc) {
        await wsc.BroadcastMessage(new plot_twist_back_end.Message() {
            type = "link",
            links = this.ArrayOfLinks(),
        },0);
    }
    
    public void AddLink(Link l, long time, WebSocketCoordinator wsc) 
    {
        this._stateOfLinks.Remove(this.inverse(l));
        if (this._stateOfLinks.ContainsKey(l)) {
            this._stateOfLinks[l] = (time, true);
        }
        else {
            this._stateOfLinks.Add(l,(time, true));
        }
        this.UpdateClientLinks(wsc);
    }

    public void RemoveLink(Link l, WebSocketCoordinator wsc) 
    {
        if (this._stateOfLinks.ContainsKey(l)) {
            this._stateOfLinks.Remove(l);
        }
        this.UpdateClientLinks(wsc);           
    }

    public void Relink(Link l, WebSocketCoordinator wsc) 
    {
        if (this._stateOfLinks.ContainsKey(l)) {
            this._stateOfLinks[l] = (this._stateOfLinks[l].Item1, true);
        }
        this.UpdateClientLinks(wsc);           
    }

    public void Unlink(Link l, WebSocketCoordinator wsc) 
    {
        if (this._stateOfLinks.ContainsKey(l)) {
            this._stateOfLinks[l] = (this._stateOfLinks[l].Item1, false);
        }
        this.UpdateClientLinks(wsc);
    }

    public plot_twist_back_end.LinkInfo[] ArrayOfLinks() {
        var list = new List<plot_twist_back_end.LinkInfo>();
        foreach (var linkTimeState in this._stateOfLinks) {
            Link link = linkTimeState.Key;
            long time = linkTimeState.Value.Item1;
            bool state = linkTimeState.Value.Item2;
            plot_twist_back_end.LinkInfo linkInfo = new plot_twist_back_end.LinkInfo() {
                action = "none",
                dataSet1 = link.DataSet1,
                dataSet2 = link.DataSet2,
                field1 = link.Field1,
                field2 = link.Field2,
                state = state,
                timeOfCreation = time,
            };
            list.Add(linkInfo);
        }
        return list.ToArray();
    }

    public plot_twist_back_end.RangeSelection[] Translate(plot_twist_back_end.RangeSelection r, String ds1, String ds2) 
    {
        List<plot_twist_back_end.RangeSelection> ret = new List<plot_twist_back_end.RangeSelection>();
        if (ds1==ds2) {
            ret.Add(r);
        }
        foreach (var linkTimeState in this._stateOfLinks) 
        {
            Link l = linkTimeState.Key;
            long time = linkTimeState.Value.Item1;
            bool state = linkTimeState.Value.Item2;
            bool matchesLink = (l.DataSet1==ds1 && l.DataSet2==ds2 && l.Field1==r.field);
            bool matchesLinkInv = (l.DataSet2 == ds1 && l.DataSet1 == ds2 && l.Field2==r.field);
            if ((matchesLink || matchesLinkInv) && state) {
                plot_twist_back_end.RangeSelection range = new plot_twist_back_end.RangeSelection() {
                    // field= r.field,
                    type = r.type,
                };
                if (r.type == "numerical") {
                    range.range = r.range;
                }
                else 
                {
                    range.categories = r.categories;
                }

                if (matchesLink) {
                    range.field = l.Field2;
                }

                if (matchesLinkInv) {
                    range.field = l.Field1;
                }
                ret.Add(range);
            }


        }
        return ret.ToArray();
    }
}
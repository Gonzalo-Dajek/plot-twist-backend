
using plot_twist_back_end.Messages;

public class rangeSet {
    private List<selection> _selectionArr = new List<selection>() { };

    public void AddSelectionArr(selection[] selectionArr) {
        for (int i = 0; i < selectionArr.Length; i++) {
            this.AddSelection(selectionArr[i]);
        }
    }
    
    public void AddSelection(selection selectionClient) {
        bool alreadyIsInArr = false;
        for (int i = 0; i < this._selectionArr.Count; i++) {
            if (this._selectionArr[i].field==selectionClient.field) {
                alreadyIsInArr = true;
                
                if (selectionClient.type == "categorical") {
                    // Intersection of category arrays
                    var existingCategories = new HashSet<string>(this._selectionArr[i].categories);
                    existingCategories.IntersectWith(selectionClient.categories);
                    var selection = this._selectionArr[i];
                    selection.categories = existingCategories.ToArray();
                    this._selectionArr[i] = selection;
                } else { // "numerical"
                    var r = this._selectionArr[i].range;
                    double start = Math.Max(r[0], selectionClient.range[0]);
                    double end = Math.Min(r[1], selectionClient.range[1]);
                    var selection = this._selectionArr[i];
                    selection.range = new double[] { start, end };
                    this._selectionArr[i] = selection;
                }
                break;
            }
        }

        if (!alreadyIsInArr) {
            this._selectionArr.Add(selectionClient);
        }
    }

    public selection[] ToArr() {
        return this._selectionArr.ToArray();
    }
}
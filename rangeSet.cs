
public class rangeSet {
    private List<plot_twist_back_end.RangeSelection> _selectionArr = new List<plot_twist_back_end.RangeSelection>() { };

    public void AddSelectionArr(plot_twist_back_end.RangeSelection[] selectionArr) {
        for (int i = 0; i < selectionArr.Length; i++) {
            this.AddSelection(selectionArr[i]);
        }
    }
    
    public void AddSelection(plot_twist_back_end.RangeSelection selectionRange) {
        bool alreadyIsInArr = false;
        for (int i = 0; i < this._selectionArr.Count; i++) {
            if (this._selectionArr[i].field==selectionRange.field) {
                alreadyIsInArr = true;
                
                if (selectionRange.type == "categorical") {
                    // Intersection of category arrays
                    var existingCategories = new HashSet<string>(this._selectionArr[i].categories);
                    existingCategories.IntersectWith(selectionRange.categories);
                    var selection = this._selectionArr[i];
                    selection.categories = existingCategories.ToArray();
                    this._selectionArr[i] = selection;
                } else { // "numerical"
                    var r = this._selectionArr[i].range;
                    double start = Math.Max(r[0], selectionRange.range[0]);
                    double end = Math.Min(r[1], selectionRange.range[1]);
                    var selection = this._selectionArr[i];
                    selection.range = new double[] { start, end };
                    this._selectionArr[i] = selection;
                }
                break;
            }
        }

        if (!alreadyIsInArr) {
            this._selectionArr.Add(selectionRange);
        }
    }

    public plot_twist_back_end.RangeSelection[] ToArr() {
        return this._selectionArr.ToArray();
    }
}

using System.Reflection.Metadata.Ecma335;

public class rangeSet {
    // _selectionArr = [];
    private List<plot_twist_back_end.RangeSelection> _selectionArr = new List<plot_twist_back_end.RangeSelection>() { };

    public void AddSelectionArr(plot_twist_back_end.RangeSelection[] selectionArr) {
        for (int i = 0; i < selectionArr.Length; i++) {
            this.AddSelection(selectionArr[i]);
        }
    }
    // addSelectionArr(selectionArr, selectionMode) {
    //     for(let i=0; i<selectionArr.length; i++){
    //         this.addSelection(selectionArr[i], selectionMode);
    //     }
    // }
    //

    public void AddSelection(plot_twist_back_end.RangeSelection selectionRange) {
        bool alreadyIsInArr = false;
        for (int i = 0; i < this._selectionArr.Count; i++) {
            if (this._selectionArr[i].field==selectionRange.field) {
                alreadyIsInArr = true;
                var r = this._selectionArr[i].range;
                double start = Math.Max(r[0], selectionRange.range[0]);
                double end = Math.Min(r[1], selectionRange.range[1]);
                var selection = this._selectionArr[i];
                selection.range = new double[] { start, end };
                this._selectionArr[i] = selection;
                break;
            }
        }

        if (!alreadyIsInArr) {
            this._selectionArr.Add(selectionRange);
        }
    }
    // addSelection(selectionRange, selectionMode){
    //     if(selectionMode!=="AND"){
    //         console.log("SELECTION MODE ERROR");
    //     }
    //
    //     let alreadyIsInArr = false;
    //     for(let i = 0; i<this._selectionArr.length; i++){
    //         if(this._selectionArr[i].field===selectionRange.field){
    //             alreadyIsInArr = true;
    //             let [x1, y1] = this._selectionArr[i].range;
    //             // interesection
    //             let start = Math.max(x1, selectionRange.range[0]);
    //             let end = Math.min(y1, selectionRange.range[1]);
    //             this._selectionArr[i].range=[start,end];
    //             break;
    //         }
    //     }
    //     if(!alreadyIsInArr){
    //         this._selectionArr.push(selectionRange);
    //         // this._selectionArr.push(JSON.parse(JSON.stringify(selectionRange))) // copy
    //     }
    // }
    //

    public plot_twist_back_end.RangeSelection[] ToArr() {
        return this._selectionArr.ToArray();
    }
    // toArr() {
    //     return this._selectionArr;
    // }
}
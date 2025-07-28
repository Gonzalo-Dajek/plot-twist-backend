
namespace plot_twist_back_end.Utils.Utils;
using plot_twist_back_end.Messages;
using System.Linq;

public class selectionSet {
    private List<dataSetSelection> _selectionArr = new List<dataSetSelection>();

    public void AddSelectionArr(dataSetSelection[]? selectionArr) {
        if(selectionArr==null) return;
        for (int i = 0; i < selectionArr.Length; i++) {
            AddSelection(selectionArr[i]);
        }
    }

    public void AddSelection(dataSetSelection selectionClient) {
        // look for existing entry by name
        int idx = _selectionArr.FindIndex(s => s.dataSetName == selectionClient.dataSetName);

        if (idx >= 0) {
            // merge via element‑wise AND
            var existing = _selectionArr[idx];
            bool[] merged = new bool[existing.indexesSelected.Length];
            for (int i = 0; i < merged.Length; i++) {
                merged[i] = existing.indexesSelected[i] && selectionClient.indexesSelected[i];
            }
            // replace with merged result
            _selectionArr[idx] = new dataSetSelection {
                dataSetName      = existing.dataSetName,
                indexesSelected  = merged
            };
        }
        else {
            _selectionArr.Add(selectionClient);
        }
    }

    public dataSetSelection[]? ToArr() {
        return _selectionArr.ToArray();
    }
}

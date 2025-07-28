using System.Text.Json;
using plot_twist_back_end.Core;

namespace plot_twist_back_end.Messages;

public struct Message
{
    public string type { get; set; }
    public ClientSelection[]? clientsSelections { get; set; }
    public DataSetInfo[]? dataSet { get; set; }
    public BenchMarkMsg? benchMark { get; set; }
    public Link[]? links { get; set; }
    public string? linksOperator { get; set; }
    public DataSetSelectionById[]? dataSetCrossSelection { get; set; }
}
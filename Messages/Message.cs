using System.Text.Json;

namespace plot_twist_back_end.Messages;

public struct Message
{
    public string type { get; set; }
    public ClientSelection[]? clientsSelections { get; set; }
    public DataSetInfo[]? dataSet { get; set; }
    public BenchMarkMsg? benchMark { get; set; }
    public Link[]? links { get; set; }
}
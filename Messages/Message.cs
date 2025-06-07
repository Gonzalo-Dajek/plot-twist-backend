namespace plot_twist_back_end.Messages;

public struct Message
{
    public string type { get; set; }
    public RangeSelection[]? range { get; set; }
    public DataSetInfo? dataSet { get; set; }
    public LinkInfo[]? links { get; set; }
    public BenchMarkMsg? benchMark { get; set; }
}
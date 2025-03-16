namespace plot_twist_back_end;
public struct Message
{
    public string type { get; set; }
    public RangeSelection[]? range { get; set; }
    public DataSetInfo? dataSet { get; set; }
    public LinkInfo[]? links { get; set; }
    public BenchMark? benchMark { get; set; }
}

public struct BenchMark {
    public string action { get; set; }
    public double timeToProcessBrushLocally { get; set; }
    public double timeToUpdatePlots { get; set; }
    public long timeSent { get; set; }
    public RangeSelection[]? range { get; set; }
    public double timeReceived { get; set; }
    public int clientId { get; set; }
    public int pingType { get; set; }
    public BenchmarkConfig? clientInfo { get; set; }

}
public struct RangeSelection
{
    public string field { get; set; }
    public string type { get; set; }
    public double[] range { get; set; } 
    public string[] categories { get; set; }
}

public struct DataSetInfo 
{
    public string name { get; set; }
    public string[] fields { get; set; }
}

public struct LinkInfo {
    public string group { get; set; }
    public string? field { get; set; }
    public string action { get; set; }
    public string dataSet { get; set; }
}

public struct BenchmarkConfig
{
    public string dataDistribution { get; set; }
    public int plotsAmount { get; set; }
    public int columnsAmount { get; set; }
    public int catColumnsAmount { get; set; }
    public int entriesAmount { get; set; }
    public int dimensionsSelected { get; set; }
    public int catDimensionsSelected { get; set; }
    public int fieldGroupsAmount { get; set; }
    public double brushSize { get; set; }
    public double stepSize { get; set; }
    public int numberOfClientBrushing { get; set; }
    public int numberOfDataSets { get; set; }
    public double testDuration { get; set; }
    public int dataSetNum { get; set; }
    public int clientId { get; set; }
}

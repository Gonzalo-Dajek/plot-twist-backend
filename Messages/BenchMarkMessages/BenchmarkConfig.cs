namespace plot_twist_back_end.Messages;

public struct BenchmarkConfig
{
    public string dataDistribution { get; set; }
    public int plotsAmount { get; set; }
    public int numColumnsAmount { get; set; }
    public int catColumnsAmount { get; set; }
    public int entriesAmount { get; set; }
    public int numDimensionsSelected { get; set; }
    public int catDimensionsSelected { get; set; }
    public int numFieldGroupsAmount { get; set; }
    public int catFieldGroupsAmount { get; set; }
    public double brushSize { get; set; }
    public double stepSize { get; set; }
    public int numberOfClientBrushing { get; set; }
    public int numberOfDataSets { get; set; }
    public double testDuration { get; set; }
    public int dataSetNum { get; set; } 
    public int clientId { get; set; }
}
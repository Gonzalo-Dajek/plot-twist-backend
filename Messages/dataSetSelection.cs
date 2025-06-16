namespace plot_twist_back_end.Messages;

public struct ClientSelection
{
    public string clientName { get; set; }
    public dataSetSelection[] selectionPerDataSet { get; set; }
}

public struct dataSetSelection
{
    public string dataSetName { get; set; }
    public selection[] selection { get; set; }
}

public struct selection
{
    public string type { get; set; }
    public string field { get; set; }
    public double[] range { get; set; } 
    public string[] categories { get; set; }
}
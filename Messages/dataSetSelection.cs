namespace plot_twist_back_end.Messages;

public struct ClientSelection
{
    public dataSetSelection[]? selectionPerDataSet { get; set; }
}

public struct dataSetSelection
{
    public string? dataSetName { get; set; }
    public bool[]? indexesSelected { get; set; }
    public int[]? color { get; set; }
}
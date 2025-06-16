namespace plot_twist_back_end.Messages;

public struct DataSetInfo 
{
    public string name { get; set; }
    public string[] fields { get; set; }
    public int? dataSetColorIndex { get; set; }
}
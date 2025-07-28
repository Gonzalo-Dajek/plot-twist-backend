using System.Text.Json;
namespace plot_twist_back_end.Messages;

public struct DataSetInfo 
{
    public string name { get; set; }
    public string[] fields { get; set; }
    public table table { get; set; }
    public int? dataSetColorIndex { get; set; }
    public int length { get; set; }
}

public class table
{
    public string[] columns { get; set; }
    public List<List<JsonElement>> rows { get; set; }
}

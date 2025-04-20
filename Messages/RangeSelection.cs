namespace plot_twist_back_end.Messages;

public struct RangeSelection
{
    public string type { get; set; }
    public string field { get; set; }
    public double[] range { get; set; } 
    public string[] categories { get; set; }
}
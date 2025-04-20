namespace plot_twist_back_end.Messages;

public struct LinkInfo {
    public string group { get; set; }
    public string? field { get; set; }
    public string action { get; set; }
    public string dataSet { get; set; }
}
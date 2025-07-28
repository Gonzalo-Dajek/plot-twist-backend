using System.Text.Json;

namespace plot_twist_back_end.Messages;

public struct Link {
    public string type { get; set; }
    public int id { get; set; }
    public bool isError { get; set; }
    public LinkState state { get; set; }
}

public struct LinkState {
    public string? dataSet1 { get; set; }
    public string? dataSet2 { get; set; }
    public string? inputField { get; set; }
}
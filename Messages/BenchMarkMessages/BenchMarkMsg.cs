namespace plot_twist_back_end.Messages;

public struct BenchMarkMsg {
    public string action { get; set; }
    public double timeToProcessBrushLocally { get; set; }
    public double timeToUpdatePlots { get; set;  }
    public int brushId { get; set; }
    public bool isActiveBrush { get; set; }
    public RangeSelection[]? range { get; set; }
    public int clientId { get; set; }
    public BenchmarkConfig? clientInfo { get; set; }
}
namespace plot_twist_back_end.Messages;

public struct BenchMark {
    public string action { get; set; }
    public double timeToProcessBrushLocally { get; set; }
    public double timeToUpdatePlots { get; set;  }
    public long timeSent { get; set; }
    public int brushId { get; set; }
    public RangeSelection[]? range { get; set; }
    public double timeReceived { get; set; }
    public int clientId { get; set; }
    public int brushClientId { get; set; }
    public int pingType { get; set; }
    public BenchmarkConfig? clientInfo { get; set; }
}
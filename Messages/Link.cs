using System.Text.Json;

namespace plot_twist_back_end.Messages;

public struct Link {
    public string linkType { get; set; }
    public JsonElement[] linkState { get; set; }
}
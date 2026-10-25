namespace ResetExplode;

/// <summary>Nó da árvore de informações do sistema.</summary>
public class InfoNode
{
    public string Header { get; set; } = "";
    public string Value { get; set; } = "";
    public List<InfoNode> Children { get; } = new();

    public string Display => string.IsNullOrEmpty(Value) ? Header : $"{Header}: {Value}";
    public bool IsGroup => Children.Count > 0;

    public InfoNode Add(string header, string value = "")
    {
        var n = new InfoNode { Header = header, Value = value };
        Children.Add(n);
        return n;
    }
}

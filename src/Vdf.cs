using System.Text;

namespace ResetExplode;

/// <summary>Nó de um arquivo VDF/ACF da Valve (formato de texto da Steam). Mantém a ordem original das chaves.</summary>
public sealed class VdfNode
{
    public List<KeyValuePair<string, object>> Items { get; } = new();

    public string? Get(string key) =>
        Items.FirstOrDefault(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && i.Value is string).Value as string;

    public VdfNode? Child(string key) =>
        Items.FirstOrDefault(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && i.Value is VdfNode).Value as VdfNode;

    public IEnumerable<KeyValuePair<string, VdfNode>> Children =>
        Items.Where(i => i.Value is VdfNode).Select(i => new KeyValuePair<string, VdfNode>(i.Key, (VdfNode)i.Value));

    public VdfNode GetOrAddChild(string key)
    {
        var c = Child(key);
        if (c != null) return c;
        c = new VdfNode();
        Items.Add(new(key, c));
        return c;
    }

    public void Set(string key, string value)
    {
        for (int i = 0; i < Items.Count; i++)
            if (Items[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase) && Items[i].Value is string)
            { Items[i] = new(Items[i].Key, value); return; }
        Items.Add(new(key, value));
    }

    public void Remove(string key) =>
        Items.RemoveAll(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && i.Value is string);
}

public static class Vdf
{
    public static VdfNode Parse(string text)
    {
        int i = 0;
        return Block(text, ref i);
    }

    private static VdfNode Block(string s, ref int i)
    {
        var node = new VdfNode();
        while (true)
        {
            string? key = Token(s, ref i, out bool close);
            if (close || key == null) return node;
            SkipSpace(s, ref i);
            if (i < s.Length && s[i] == '{')
            {
                i++;
                node.Items.Add(new(key, Block(s, ref i)));
            }
            else
            {
                string? val = Token(s, ref i, out _);
                node.Items.Add(new(key, val ?? ""));
            }
        }
    }

    private static void SkipSpace(string s, ref int i)
    {
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) i++;
            else if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
            }
            else break;
        }
    }

    private static string? Token(string s, ref int i, out bool close)
    {
        close = false;
        SkipSpace(s, ref i);
        if (i >= s.Length) return null;
        if (s[i] == '}') { i++; close = true; return null; }
        if (s[i] == '"')
        {
            i++;
            var sb = new StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                    sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', var c => c });
                }
                else sb.Append(s[i]);
                i++;
            }
            i++; // aspas finais
            return sb.ToString();
        }
        int start = i;
        while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '{' && s[i] != '}' && s[i] != '"') i++;
        return s[start..i];
    }

    private static string Esc(string v) => v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t");

    public static string Serialize(VdfNode root)
    {
        var sb = new StringBuilder();
        Write(sb, root, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, VdfNode n, int depth)
    {
        string ind = new('\t', depth);
        foreach (var (k, v) in n.Items)
        {
            if (v is string str) sb.Append(ind).Append('"').Append(Esc(k)).Append("\"\t\t\"").Append(Esc(str)).Append("\"\n");
            else if (v is VdfNode child)
            {
                sb.Append(ind).Append('"').Append(Esc(k)).Append("\"\n").Append(ind).Append("{\n");
                Write(sb, child, depth + 1);
                sb.Append(ind).Append("}\n");
            }
        }
    }
}

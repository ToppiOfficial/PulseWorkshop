namespace PulseWorkshop.Core.Unpack;

/// <summary>One node in a Valve KeyValues document: a key with either a value or children.</summary>
public sealed class KvNode
{
    public string Key = string.Empty;
    public string Value = string.Empty;
    public List<KvNode> Children = new();
}

/// <summary>
/// Minimal Valve KeyValues (VDF) parser - a C# port of ModelTool's kv_parser.cpp, kept
/// behavior-identical (line comments, quoted + bare tokens, lenient escapes). Parse-only;
/// used to read gameinfo.txt for the Unpack tab.
/// </summary>
public static class KeyValues
{
    public static KvNode Parse(string source)
    {
        var tokens = Tokenize(source);
        int pos = 0;
        return new KvNode { Children = ParseChildren(tokens, ref pos) };
    }

    /// <summary>Finds the first child with the given key, case-insensitively.</summary>
    public static KvNode? Find(IEnumerable<KvNode> nodes, string key) =>
        nodes.FirstOrDefault(n => string.Equals(n.Key, key, StringComparison.OrdinalIgnoreCase));

    private enum TokType { String, OpenBrace, CloseBrace, Eof }
    private readonly record struct Token(TokType Type, string Value);

    private static List<Token> Tokenize(string src)
    {
        var tokens = new List<Token>();
        int i = 0;
        int n = src.Length;

        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(src[i])) i++;
            if (i >= n) break;

            // Line comment
            if (i + 1 < n && src[i] == '/' && src[i + 1] == '/')
            {
                while (i < n && src[i] != '\n') i++;
                continue;
            }

            if (src[i] == '{') { tokens.Add(new(TokType.OpenBrace, string.Empty)); i++; continue; }
            if (src[i] == '}') { tokens.Add(new(TokType.CloseBrace, string.Empty)); i++; continue; }

            if (src[i] == '"')
            {
                i++;
                var val = new System.Text.StringBuilder();
                while (i < n && src[i] != '"')
                {
                    if (src[i] == '\\' && i + 1 < n)
                    {
                        char next = src[i + 1];
                        switch (next)
                        {
                            case 'n': val.Append('\n'); break;
                            case 't': val.Append('\t'); break;
                            // Keep unknown escapes verbatim (e.g. backslashes in paths).
                            default: val.Append('\\').Append(next); break;
                        }
                        i += 2;
                    }
                    else
                    {
                        val.Append(src[i++]);
                    }
                }
                if (i < n) i++; // skip closing "
                tokens.Add(new(TokType.String, val.ToString()));
                continue;
            }

            // Unquoted token - stops at whitespace, braces, or quotes
            int start = i;
            while (i < n && !char.IsWhiteSpace(src[i]) && src[i] != '{' && src[i] != '}' && src[i] != '"')
                i++;
            if (i > start)
                tokens.Add(new(TokType.String, src[start..i]));
        }

        tokens.Add(new(TokType.Eof, string.Empty));
        return tokens;
    }

    private static List<KvNode> ParseChildren(List<Token> toks, ref int pos)
    {
        var result = new List<KvNode>();
        while (pos < toks.Count && toks[pos].Type is not (TokType.CloseBrace or TokType.Eof))
        {
            if (toks[pos].Type != TokType.String) { pos++; continue; }

            var node = new KvNode { Key = toks[pos++].Value };

            if (pos >= toks.Count || toks[pos].Type == TokType.Eof)
            {
                result.Add(node);
                break;
            }

            if (toks[pos].Type == TokType.OpenBrace)
            {
                pos++; // consume {
                node.Children = ParseChildren(toks, ref pos);
                if (pos < toks.Count && toks[pos].Type == TokType.CloseBrace) pos++;
            }
            else if (toks[pos].Type == TokType.String)
            {
                node.Value = toks[pos++].Value;
            }

            result.Add(node);
        }
        return result;
    }
}

using System.Text.RegularExpressions;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// Reusable text filter for the Published / Drafts / Templates lists. Plain case-insensitive
/// substring match by default; set <see cref="UseRegex"/> for regex. Matches any of the supplied
/// candidate strings (caller decides which fields - title, tags, description, workshop id).
/// </summary>
public sealed class ItemFilter
{
    private string _query = string.Empty;
    private bool _useRegex;
    private Regex? _compiled;
    private bool _regexValid = true;

    public string Query
    {
        get => _query;
        set { _query = value ?? string.Empty; Recompile(); }
    }

    public bool UseRegex
    {
        get => _useRegex;
        set { _useRegex = value; Recompile(); }
    }

    /// <summary>False when regex mode is on and the pattern is currently invalid (mid-typing).</summary>
    public bool RegexValid => _regexValid;

    public bool IsEmpty => string.IsNullOrWhiteSpace(_query);

    private void Recompile()
    {
        _compiled = null;
        _regexValid = true;

        if (!_useRegex || string.IsNullOrEmpty(_query))
            return;

        try
        {
            _compiled = new Regex(_query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            // Incomplete/invalid pattern while typing - treat as "matches nothing useful" but
            // don't throw; the UI can flag RegexValid=false.
            _regexValid = false;
        }
    }

    /// <summary>True if any of the candidate fields matches the current query.</summary>
    public bool Matches(params string?[] candidates)
    {
        if (IsEmpty)
            return true;

        if (_useRegex)
        {
            if (!_regexValid || _compiled is null)
                return false;
            foreach (var c in candidates)
                if (c is not null && _compiled.IsMatch(c))
                    return true;
            return false;
        }

        foreach (var c in candidates)
            if (c is not null && c.Contains(_query, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

using System.Windows;
using System.Windows.Controls;
using PulseWorkshop.Core.Storage;

namespace PulseWorkshop.App.Services;

/// <summary>
/// App-wide persistence of splitter pane sizes. Tag any <see cref="ColumnDefinition"/> or
/// <see cref="RowDefinition"/> a GridSplitter resizes with <c>svc:PaneSize.Key="unpack.tree"</c>
/// and its size is restored when the element loads and written back by <see cref="Save"/> on close.
/// <para>
/// Sizes round-trip as GridLength text ("320", "2.4*"), so a star-sized pane stays proportional.
/// When a splitter resizes two <i>star</i> columns, tag <b>both</b> - restoring only one would
/// change the ratio against the other's unrestored default.
/// </para>
/// </summary>
public static class PaneSize
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(PaneSize), new PropertyMetadata(null, OnKeyChanged));

    public static void SetKey(DependencyObject d, string value) => d.SetValue(KeyProperty, value);
    public static string? GetKey(DependencyObject d) => (string?)d.GetValue(KeyProperty);

    private static readonly Dictionary<string, DefinitionBase> Tracked = new();
    private static readonly GridLengthConverter Converter = new();
    private static UiSettings? _settings;

    /// <summary>Supplies the settings instance the sizes are read from. Must be called <b>before</b>
    /// <c>InitializeComponent</c>, because the attached property restores as the XAML is parsed.</summary>
    public static void Attach(UiSettings settings) => _settings = settings;

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DefinitionBase def || e.NewValue is not string key)
            return;
        Tracked[key] = def;

        if (_settings is null || !_settings.PaneSizes.TryGetValue(key, out var saved))
            return;
        if (Parse(saved) is not { } length)
            return;
        if (def is ColumnDefinition column)
            column.Width = length;
        else if (def is RowDefinition row)
            row.Height = length;
    }

    /// <summary>Copies every tagged pane's current size into the settings (the caller saves).</summary>
    public static void Save(UiSettings settings)
    {
        foreach (var (key, def) in Tracked)
        {
            var length = def switch
            {
                ColumnDefinition c => c.Width,
                RowDefinition r => r.Height,
                _ => GridLength.Auto,
            };
            // Auto panes have nothing to remember; a zero one is collapsed (a closed pane), and
            // restoring that would hide it with no way back.
            if (length.IsAuto || length.Value <= 0)
                continue;
            if (Converter.ConvertToInvariantString(length) is { } text)
                settings.PaneSizes[key] = text;
        }
    }

    private static GridLength? Parse(string text)
    {
        try
        {
            return Converter.ConvertFromInvariantString(text) as GridLength?;
        }
        catch
        {
            return null; // Hand-edited or corrupt settings - keep the XAML default.
        }
    }
}

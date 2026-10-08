using Microsoft.UI.Text;
using Windows.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace Varlatch;

/// <summary>Small builders for the panel and Settings, which are made in code.</summary>
internal static class Ui
{
    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    public static TextBlock Text(string text, string style = "VarlatchBodyText", bool select = false) => new()
    {
        Text = text,
        Style = Style(style),
        IsTextSelectionEnabled = select,
    };

    public static TextBlock Title(string text) => Text(text, "VarlatchTitleText");

    public static TextBlock Caption(string text, bool error = false, bool select = false) =>
        Text(text, error ? "VarlatchErrorText" : "VarlatchCaptionText", select);

    public static Button Button(string text, Action click, bool accent = false, string? tip = null)
    {
        var button = new Button
        {
            Content = text,
            Style = Style(accent ? "VarlatchAccentButton" : "VarlatchButton"),
        };
        button.Click += (_, _) => click();
        if (tip is not null) ToolTipService.SetToolTip(button, tip);
        return button;
    }

    /// <summary>A borderless button with a glyph from Segoe Fluent Icons, and its name for screen readers.</summary>
    public static Button IconButton(string glyph, string tip, Action click)
    {
        var button = new Button
        {
            Content = Glyph(glyph, 14),
            Style = Style("VarlatchSubtleButton"),
        };
        button.Click += (_, _) => click();
        ToolTipService.SetToolTip(button, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tip);
        return button;
    }

    /// <summary>A borderless button with a glyph and a label, such as "+ Add server".</summary>
    public static Button LinkButton(string glyph, string text, Action click, string? tip = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(Glyph(glyph, 12));
        content.Children.Add(new TextBlock { Text = text, FontSize = 13 });
        var button = new Button { Content = content, Style = Style("VarlatchSubtleButton") };
        button.Click += (_, _) => click();
        if (tip is not null) ToolTipService.SetToolTip(button, tip);
        return button;
    }

    public static FontIcon Glyph(string glyph, double size = 16, string style = "VarlatchSecondaryGlyph") => new()
    {
        Glyph = glyph,
        Style = Style(style),
        FontSize = size,
    };

    public static Border Divider(double horizontalMargin = 0, double verticalMargin = 0) => new()
    {
        Style = Style("VarlatchDivider"),
        Margin = new Thickness(horizontalMargin, verticalMargin, horizontalMargin, verticalMargin),
    };

    public static Ellipse Dot(string style) => new() { Width = 8, Height = 8, Style = Style(style) };

    /// <summary>Buttons side by side; <paramref name="right"/> lines them up on the right.</summary>
    public static StackPanel Buttons(bool right, params UIElement[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }

    public static ProgressRing Spinner(double size = 16) => new()
    {
        IsActive = true,
        Width = size,
        Height = size,
        MinWidth = 0,
        MinHeight = 0,
    };

    /// <summary>Sets a panel's children only when they differ, so an open menu or a focused box keeps its place.</summary>
    public static void SetChildren(Panel panel, IReadOnlyList<UIElement> children)
    {
        var current = panel.Children;
        if (current.Count == children.Count && current.Zip(children).All(p => ReferenceEquals(p.First, p.Second))) return;
        foreach (var child in children)
        {
            // An element can have one parent: take it from the last one first.
            if (child is FrameworkElement { Parent: Panel parent } && !ReferenceEquals(parent, panel)) parent.Children.Remove(child);
        }
        current.Clear();
        foreach (var child in children) current.Add(child);
    }

    public static FontWeight SemiBold => FontWeights.SemiBold;

    /// <summary>Segoe Fluent Icons.</summary>
    public static class Glyphs
    {
        public const string More = "";
        public const string Settings = "";
        public const string Power = "";
        public const string Add = "";
        public const string Terminal = "";
        public const string Warning = "";
        public const string Contact = "";
        public const string Download = "";
        public const string Code = "";
        public const string Pin = "";
        public const string Sync = "";
        public const string Copy = "";
        public const string Link = "";
    }
}

using System;
using System.Windows;
using System.Windows.Controls;

namespace Talos.App;

internal sealed class OverlayWindow : Window
{
    private readonly TextBlock text = new() { TextWrapping = TextWrapping.Wrap, FontSize = 17, FontWeight = FontWeights.SemiBold, LineHeight = 27 };
    private readonly TextBlock state = new() { Text = "等待截图", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    public bool AllowClose { get; set; }
    public OverlayWindow(Action expand, Action capture)
    {
        Title = "典狱建议 · F8读取"; Width = 430; Height = 294; MinWidth = 340; MinHeight = 260;
        Topmost = true; ShowActivated = false;
        Theme.Apply(this);

        var root = new Grid { Margin = new Thickness(18, 15, 18, 14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var next = new TextBlock { Text = "下一步建议", FontSize = 14, FontWeight = FontWeights.SemiBold };
        next.SetResourceReference(TextBlock.ForegroundProperty, "Talos.Text"); header.Children.Add(next);
        state.SetResourceReference(TextBlock.ForegroundProperty, "Talos.Gold"); Grid.SetColumn(state, 1); header.Children.Add(state); root.Children.Add(header);

        var recommendation = new Border { CornerRadius = new CornerRadius(11), Padding = new Thickness(13, 10, 13, 10), Margin = new Thickness(0, 11, 0, 12), BorderThickness = new Thickness(1) };
        recommendation.SetResourceReference(Border.BackgroundProperty, "Talos.Surface"); recommendation.SetResourceReference(Border.BorderBrushProperty, "Talos.Border");
        text.SetResourceReference(TextBlock.ForegroundProperty, "Talos.Text");
        recommendation.Child = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
        Grid.SetRow(recommendation, 1); root.Children.Add(recommendation);

        var buttons = new Grid(); buttons.ColumnDefinitions.Add(new ColumnDefinition()); buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var read = new Button { Content = "F8  读取画面", Padding = new Thickness(14, 8, 14, 8), MinHeight = 38, Margin = new Thickness(0, 0, 9, 0) };
        read.SetResourceReference(StyleProperty, "Talos.PrimaryButton"); read.Click += (_, _) => capture(); buttons.Children.Add(read);
        var open = new Button { Content = "展开核对", Padding = new Thickness(15, 8, 15, 8), MinHeight = 38 };
        open.SetResourceReference(StyleProperty, "Talos.SecondaryButton"); open.Click += (_, _) => expand(); Grid.SetColumn(open, 1); buttons.Children.Add(open);
        Grid.SetRow(buttons, 2); root.Children.Add(buttons);

        var footnote = new TextBlock { Text = "离线建议 · 请先核对棋盘，评分不代表绝对最优。", FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(1, 10, 0, 0), LineHeight = 16 };
        footnote.SetResourceReference(TextBlock.ForegroundProperty, "Talos.Muted"); Grid.SetRow(footnote, 3); root.Children.Add(footnote);
        Content = root;
        Closing += (_, args) => { if (!AllowClose) { args.Cancel = true; Hide(); expand(); } };
    }
    public void SetText(string message) => text.Text = message;
    public void SetState(string label, string message) { state.Text = label; text.Text = message; }
}

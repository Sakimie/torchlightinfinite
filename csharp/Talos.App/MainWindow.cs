using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Talos.Core;
using Talos.Core.Vision;
using Talos.Updates;

namespace Talos.App;

public sealed partial class MainWindow : Window
{
    private readonly LaunchContext context;
    private readonly ObservableCollection<RoomEditor> rooms = new();
    private readonly ComboBox sources = new() { MinWidth = 150, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly DataGrid board = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, RowHeight = 30, HeadersVisibility = DataGridHeadersVisibility.Column };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock sourceLabel = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox output = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 48, Height = 68 };
    private readonly Image preview = new() { Stretch = Stretch.Uniform };
    private readonly Canvas marks = new() { Background = Brushes.Transparent, ClipToBounds = true };
    private readonly Grid previewGrid = new() { MinHeight = 80 };
    private readonly Grid previewEmpty = new() { IsHitTestVisible = false };
    private readonly CheckBox confirmed = new() { Content = "我已逐格核对画面和全部控制信息", Margin = new Thickness(0, 4, 12, 4) };
    private readonly CheckBox layoutConfirmed = new() { Content = "16个房间中心已核对", Margin = new Thickness(6) };
    private readonly TextBox day = Box(""), vault = Box("0"), progress = Box("0"), chainConsumed = Box("");
    private readonly ComboBox position = new(), phase = new(), objective = new(), usurp = new(), currentEvent = new(), resume = new();
    private readonly CheckBox milestone = Check("任务里程碑已完成"), used = Check("本日管理操作已使用"), chain = Check("连锁反应效果已激活"), noSwap = Check("当前不能替换"), noDevour = Check("当前不能吞噬");
    private readonly CheckBox unsupported = Check("存在列表外刑具/事件、猎物、特殊开局、额外操作或已探知隐藏信息（当前不支持）");
    private readonly ListBox tools = new() { SelectionMode = SelectionMode.Multiple }, offers = new() { SelectionMode = SelectionMode.Multiple };
    private readonly Button advise = new() { Content = "已核对，计算下一步" }, recognize = new() { Content = "离线识别当前截图" }, capture = new() { Content = "F8 读取游戏" };
    private readonly ComboBox adviceMode = new() { MinWidth = 150 };
    private OverlayWindow? overlay;
    private UpdateWindow? updateWindow;
    private BitmapSource? currentImage;
    private VisionCalibration calibration = VisionCalibration.CreateLegacyLayout();
    private ScreenshotRecognizer? recognizer;
    private CancellationTokenSource? work;
    private CancellationTokenSource? captureWork;
    private long revision;
    private bool suppress, captureBusy, closed, closing, compactMode, refreshingSources;
    private WindowState lastNonMinimizedState = WindowState.Normal;
    private long updateWindowGeneration;
    private (WindowState State, long Generation)? pendingUpdateReturn;
    private string source = "manual", calibrationMode = "";
    private string? overlaySummary;
    private NormalizedPoint? dragStart;
    private readonly List<NormalizedPoint> clickedCenters = new();
    private nint handle;
    private HwndSource? hwndSource;

    public MainWindow(LaunchContext context)
    {
        this.context = context;
        Title = $"典狱助手 {context.Version}"; MinWidth = 900; MinHeight = 580;
        Width = Math.Min(1320, SystemParameters.WorkArea.Width - 32); Height = Math.Min(900, SystemParameters.WorkArea.Height - 32);
        Theme.Apply(this); WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Build();
        LoadState(GameAdvisor.CreateEmpty(), blankDay: true);
        LoadCalibration(); RefreshSources(); InitializeFollowing();
        sources.SelectionChanged += (_, _) => { if (!refreshingSources && !suppress) ResetSource(); };
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            hwndSource = HwndSource.FromHwnd(handle); hwndSource?.AddHook(WindowMessage);
            if (!NativeCapture.Register(handle)) status.Text = "全局F8被其他程序占用。仍可点击读取按钮或导入图片；请关闭旧版助手后重试。";
        };
        PreviewKeyDown += async (_, e) => { if (e.Key == Key.F8) { e.Handled = true; await CaptureAsync(); } };
        Closing += (_, _) => { closing = true; ShutdownFollowing(); captureWork?.Cancel(); work?.Cancel(); };
        StateChanged += (_, _) => { if (!closing && !captureBusy && updateWindow is null && WindowState != WindowState.Minimized) lastNonMinimizedState = WindowState; };
        Closed += (_, _) => { closed = true; work?.Cancel(); NativeCapture.Unregister(handle); hwndSource?.RemoveHook(WindowMessage); if (overlay is not null) { overlay.AllowClose = true; overlay.Close(); } };
    }

    private T Styled<T>(T element, string key) where T : FrameworkElement { element.SetResourceReference(StyleProperty, key); return element; }
    private Brush ColorOf(string key) => (Brush)FindResource(key);
    private TextBlock Caption(string text, string style = "Talos.MutedText") => Styled(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, style);
    private Border Card(UIElement child, double padding = 14) => Styled(new Border { Child = child, Padding = new Thickness(padding) }, "Talos.Card");
    private static TextBox Box(string text) => new() { Text = text, MinWidth = 100 };
    private static CheckBox Check(string text) => new() { Content = text, Margin = new Thickness(3, 6, 3, 6), VerticalContentAlignment = VerticalAlignment.Center };
    private static Button Button(string text, Action action) { var button = new Button { Content = text }; button.Click += (_, _) => action(); return button; }
    private static Choice[] Choices(IReadOnlyDictionary<string, string> values, bool empty = false) => (empty ? new[] { new Choice("", "未确认 / 无") } : Array.Empty<Choice>()).Concat(values.Select(item => new Choice(item.Key, item.Value))).ToArray();
    private static void SetItems(ComboBox combo, IEnumerable<Choice> values) { combo.ItemsSource = values; combo.DisplayMemberPath = "Name"; combo.SelectedValuePath = "Id"; }
    private static string Selected(ComboBox combo) => combo.SelectedValue as string ?? "";

    private void Build()
    {
        var main = new Grid { Margin = new Thickness(18, 12, 18, 10), Height = 620 };
        for (int row = 0; row < 5; row++) main.RowDefinitions.Add(new RowDefinition { Height = row == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        var header = new Grid { Margin = new Thickness(1, 0, 1, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var identity = new StackPanel();
        var brand = new StackPanel { Orientation = Orientation.Horizontal };
        brand.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = ColorOf("Talos.Accent"), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center });
        brand.Children.Add(Styled(new TextBlock { Text = "典狱助手", FontSize = 23 }, "Talos.Title"));
        brand.Children.Add(new TextBlock { Text = $"v{context.Version}", Foreground = ColorOf("Talos.Muted"), Margin = new Thickness(12, 0, 0, 1), VerticalAlignment = VerticalAlignment.Bottom, FontSize = 11 });
        identity.Children.Add(brand); identity.Children.Add(Caption("普通 20 天  /  优先绮彩  /  本机分析")); header.Children.Add(identity);
        var utilities = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        utilities.Children.Add(Styled(Button("精简悬浮", ShowCompact), "Talos.SecondaryButton"));
        utilities.Children.Add(Styled(Button("检查更新", ShowUpdates), "Talos.GhostButton")); Grid.SetColumn(utilities, 1); header.Children.Add(utilities); main.Children.Add(header);
        utilities.Children.Insert(0, BuildFollowControls());

        var toolbar = new StackPanel();
        var sourceRow = new Grid(); sourceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); sourceRow.ColumnDefinitions.Add(new ColumnDefinition()); sourceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sourceTitle = Caption("截图来源", "Talos.Label"); sourceTitle.VerticalAlignment = VerticalAlignment.Center; sourceTitle.Margin = new Thickness(0, 0, 12, 0); sourceRow.Children.Add(sourceTitle);
        var sourceText = new FrameworkElementFactory(typeof(TextBlock)); sourceText.SetBinding(TextBlock.TextProperty, new Binding("Name")); sourceText.SetBinding(ToolTipProperty, new Binding("Name")); sourceText.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); sourceText.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap); sourceText.SetValue(MaxWidthProperty, 420d);
        sources.ItemTemplate = new DataTemplate { VisualTree = sourceText }; sources.HorizontalContentAlignment = HorizontalAlignment.Stretch; sources.SetBinding(ToolTipProperty, new Binding("SelectedItem.Name") { Source = sources });
        sources.Margin = new Thickness(0, 2, 8, 2); Grid.SetColumn(sources, 1); sourceRow.Children.Add(sources);
        var captureActions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        captureActions.Children.Add(Styled(Button("刷新", RefreshSources), "Talos.GhostButton"));
        capture.Click += async (_, _) => await CaptureAsync(); Styled(capture, "Talos.PrimaryButton"); captureActions.Children.Add(capture);
        captureActions.Children.Add(Button("导入截图", async () => await ImportAsync())); captureActions.Children.Add(Button("粘贴图片", async () => await PasteAsync()));
        Grid.SetColumn(captureActions, 2); sourceRow.Children.Add(captureActions); toolbar.Children.Add(sourceRow);
        var sourceInfo = new Grid { Margin = new Thickness(0, 4, 0, 0) }; sourceInfo.ColumnDefinitions.Add(new ColumnDefinition()); sourceInfo.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sourceLabel.Foreground = ColorOf("Talos.Gold"); sourceLabel.FontSize = 11; sourceLabel.Margin = new Thickness(1, 0, 8, 0); sourceInfo.Children.Add(sourceLabel);
        var research = new WrapPanel();
        research.Children.Add(Styled(Button("示例 · 非实况", () => { StopFollowing("已进入示例研究，日志跟随已停止。", false); captureWork?.Cancel(); ClearImage(); source = "example"; LoadState(GameAdvisor.CreateExample()); SourceChanged(); }), "Talos.GhostButton"));
        research.Children.Add(Styled(Button("新局 / 清空", () => { StopFollowing("已手动清空局面，日志跟随已停止。", false); captureWork?.Cancel(); ClearImage(); source = "manual"; LoadState(GameAdvisor.CreateEmpty(), true); SourceChanged(); }), "Talos.GhostButton"));
        foreach (Button button in research.Children) { button.MinHeight = 22; button.Padding = new Thickness(7, 2, 7, 2); button.Margin = new Thickness(2, 0, 2, 0); button.FontSize = 11; }
        Grid.SetColumn(research, 1); sourceInfo.Children.Add(research); toolbar.Children.Add(sourceInfo);
        var toolbarCard = Card(toolbar, 10); toolbarCard.Margin = new Thickness(0, 0, 0, 10); Grid.SetRow(toolbarCard, 1); main.Children.Add(toolbarCard);

        var tabs = new TabControl { Padding = new Thickness(0) };
        tabs.Items.Add(new TabItem { Header = "01  画面与校准", Content = BuildCapture() });
        tabs.Items.Add(new TabItem { Header = "02  牢房与状态", Content = BuildReview() });
        tabs.Items.Add(new TabItem { Header = "03  刑具与奖励", Content = BuildTools() });
        var workspace = Card(tabs, 8); Grid.SetRow(workspace, 2); main.Children.Add(workspace);

        var advice = new Grid(); advice.ColumnDefinitions.Add(new ColumnDefinition()); advice.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        var result = new StackPanel { Margin = new Thickness(0, 0, 18, 0) }; var adviceHeading = new DockPanel();
        SetItems(adviceMode, new[] { new Choice("rollout", "整局模拟 · 有限采样"), new Choice("instant", "即时评分 · 单步规则") }); adviceMode.SelectedValue = "rollout"; adviceMode.FontSize = 11; adviceMode.ToolTip = "整局模拟以支持规则下的终局绮彩数比较动作，默认最多64轮或约1.8秒；即时评分只比较当前一步。两种模式均不是最优保证。"; adviceMode.SelectionChanged += (_, _) => Invalidate("计算模式已切换，请重新核对并计算。"); DockPanel.SetDock(adviceMode, Dock.Right); adviceHeading.Children.Add(adviceMode);
        var adviceTitle = Caption("下一步建议", "Talos.Label"); adviceTitle.FontSize = 15; adviceTitle.VerticalAlignment = VerticalAlignment.Center; adviceHeading.Children.Add(adviceTitle); result.Children.Add(adviceHeading);
        var review = new StackPanel(); confirmed.Content = new TextBlock { Text = "我已逐格核对画面和全部控制信息", TextWrapping = TextWrapping.Wrap }; review.Children.Add(confirmed);
        confirmed.Click += (_, _) => { if (confirmed.IsChecked != true) Invalidate("核对确认已撤销，旧建议不再适用。"); };
        var reviewActions = new WrapPanel { Margin = new Thickness(0, 1, 0, 3) };
        advise.Click += async (_, _) => await RecommendAsync(); Styled(advise, "Talos.PrimaryButton"); reviewActions.Children.Add(advise);
        reviewActions.Children.Add(Styled(Button("取消分析", CancelCurrentOperation), "Talos.GhostButton")); review.Children.Add(reviewActions);
        status.Foreground = ColorOf("Talos.Muted"); status.FontSize = 11;
        review.Children.Add(new ScrollViewer { Content = status, MaxHeight = 30, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); Grid.SetColumn(review, 1); advice.Children.Add(review);
        output.Text = "先获取画面、完整核对局面，再计算下一步。程序不会点击或操作游戏。";
        output.Foreground = ColorOf("Talos.Text"); output.Background = Brushes.Transparent; output.BorderThickness = new Thickness(0); output.Padding = new Thickness(0, 5, 0, 0); result.Children.Add(output); advice.Children.Add(result);
        var adviceCard = Card(advice, 12); adviceCard.Margin = new Thickness(0, 10, 0, 5); Grid.SetRow(adviceCard, 3); main.Children.Add(adviceCard);

        var details = new Expander { Header = "使用说明与数据目录", FontSize = 11, Foreground = ColorOf("Talos.Muted") };
        var detailsContent = new StackPanel { Margin = new Thickness(8, 6, 8, 4) };
        detailsContent.Children.Add(Caption("离线使用：截图和建议均在本机处理，仅主动检查或下载更新时联网。识图只作预填，未知格和控制信息须人工核对。"));
        detailsContent.Children.Add(Caption("策略边界：整局模拟只覆盖已支持规则，采样均值和标准误不保证最优；即时评分只看当前一步。两者均未验证实机收益，识图结果须人工核对。"));
        var dataDirectory = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var openDataDirectory = Styled(Button("打开数据目录", OpenDataDirectory), "Talos.GhostButton"); DockPanel.SetDock(openDataDirectory, Dock.Right); dataDirectory.Children.Add(openDataDirectory);
        dataDirectory.Children.Add(new TextBox { Text = context.DataRoot, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 48, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ToolTip = "实际数据目录，可选中并复制路径" }); detailsContent.Children.Add(dataDirectory);
        details.Content = new ScrollViewer { Content = detailsContent, MaxHeight = 125, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(details, 4); main.Children.Add(details);
        var shell = new ScrollViewer { Content = main, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        void FitWorkspace() => main.Height = Math.Max(details.IsExpanded ? 750 : 620, shell.ActualHeight - main.Margin.Top - main.Margin.Bottom);
        shell.SizeChanged += (_, _) => FitWorkspace(); details.Expanded += (_, _) => FitWorkspace(); details.Collapsed += (_, _) => FitWorkspace(); Content = shell;
    }

    private UIElement BuildCapture()
    {
        var panel = new DockPanel { Margin = new Thickness(5, 9, 5, 5) };
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var buttons = new WrapPanel(); buttons.Children.Add(Button("框选棋盘区域", () => { if (currentImage is null) { status.Text = "请先获取截图。"; return; } calibrationMode = "roi"; status.Text = "在预览中拖动，框选完整游戏界面区域；随后核对或逐格标定房间中心。"; }));
        buttons.Children.Add(Button("标定16个中心", () => { if (currentImage is null) { status.Text = "请先获取截图。"; return; } clickedCenters.Clear(); calibrationMode = "points"; layoutConfirmed.IsChecked = false; status.Text = "请点击01审讯室中心，再沿顺时针依次点击至16号。"; DrawCalibration(); }));
        buttons.Children.Add(Styled(Button("撤销一个", () => { if (clickedCenters.Count > 0) clickedCenters.RemoveAt(clickedCenters.Count - 1); calibrationMode = "points"; layoutConfirmed.IsChecked = false; DrawCalibration(); }), "Talos.GhostButton"));
        layoutConfirmed.FontSize = 11; buttons.Children.Add(layoutConfirmed); buttons.Children.Add(Button("保存校准", SaveCalibration));
        recognize.Click += async (_, _) => await RecognizeAsync(); buttons.Children.Add(recognize); top.Children.Add(buttons);
        var captureHint = Caption("识图只预填兽种和品质候选。未知格不能当空房；天数、位置、刑具和事件需手动核对。"); captureHint.FontSize = 11; captureHint.Margin = new Thickness(3, 4, 3, 0); top.Children.Add(captureHint);
        DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        previewGrid.Background = ColorOf("Talos.Background"); previewGrid.Children.Add(preview); previewEmpty.Children.Add(BuildEmptyPreview()); previewGrid.Children.Add(previewEmpty); previewGrid.Children.Add(marks);
        panel.Children.Add(new Border { Child = previewGrid, CornerRadius = new CornerRadius(10), BorderBrush = ColorOf("Talos.Border"), BorderThickness = new Thickness(1), ClipToBounds = true });
        marks.MouseLeftButtonDown += PreviewDown; marks.MouseMove += PreviewMove; marks.MouseLeftButtonUp += PreviewUp;
        previewGrid.SizeChanged += (_, _) => DrawCalibration();
        layoutConfirmed.Checked += (_, _) => { if (calibrationMode == "points" && clickedCenters.Count != 16) { layoutConfirmed.IsChecked = false; status.Text = "必须标全16个中心才能确认，请继续点击或重新标定。"; return; } calibration = calibration with { LayoutConfirmed = true }; Invalidate("校准已确认，仍需核对本张截图。", cancelWork: true); };
        layoutConfirmed.Unchecked += (_, _) => { calibration = calibration with { LayoutConfirmed = false }; if (!suppress) Invalidate("校准尚未确认，不能自动识别。"); };
        return panel;
    }

    private UIElement BuildEmptyPreview()
    {
        var content = new StackPanel { Width = 430, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var diagram = new Grid { Width = 290, Height = 156, Margin = new Thickness(0, 0, 0, 14) };
        for (int column = 0; column < 6; column++) diagram.ColumnDefinitions.Add(new ColumnDefinition());
        for (int row = 0; row < 4; row++) diagram.RowDefinitions.Add(new RowDefinition());
        (int X, int Y)[] coordinates = Enumerable.Range(0, 6).Select(column => (column, 0)).Concat(new[] { (5, 1), (5, 2) }).Concat(Enumerable.Range(0, 6).Select(column => (5 - column, 3))).Concat(new[] { (0, 2), (0, 1) }).ToArray();
        for (int index = 0; index < coordinates.Length; index++)
        {
            var room = new Border { Margin = new Thickness(3), CornerRadius = new CornerRadius(6), Background = ColorOf("Talos.Surface"), BorderBrush = ColorOf(index == 0 ? "Talos.Gold" : "Talos.Border"), BorderThickness = new Thickness(1), Child = new TextBlock { Text = (index + 1).ToString("00"), Foreground = ColorOf(index == 0 ? "Talos.Gold" : "Talos.Muted"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            Grid.SetColumn(room, coordinates[index].X); Grid.SetRow(room, coordinates[index].Y); diagram.Children.Add(room);
        }
        var center = Caption("16 间房 · 顺时针核对", "Talos.MutedText"); center.HorizontalAlignment = HorizontalAlignment.Center; center.VerticalAlignment = VerticalAlignment.Center; center.FontSize = 11; Grid.SetColumn(center, 1); Grid.SetRow(center, 1); Grid.SetColumnSpan(center, 4); Grid.SetRowSpan(center, 2); diagram.Children.Add(center); content.Children.Add(diagram);
        var heading = Caption("把这一局的画面带进来", "Talos.Label"); heading.FontSize = 18; heading.TextAlignment = TextAlignment.Center; content.Children.Add(heading);
        var instruction = Caption("按 F8 读取游戏，或导入 / 粘贴截图"); instruction.Margin = new Thickness(0, 7, 0, 8); instruction.TextAlignment = TextAlignment.Center; content.Children.Add(instruction);
        var steps = Caption("获取画面  ›  校准位置  ›  核对后计算"); steps.TextAlignment = TextAlignment.Center; steps.FontSize = 11; content.Children.Add(steps);
        return new Viewbox { Child = content, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(16) };
    }

    private UIElement BuildReview()
    {
        var layout = new Grid { Margin = new Thickness(5, 9, 5, 5) }; layout.ColumnDefinitions.Add(new ColumnDefinition()); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(264) });
        var left = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        var help = new StackPanel { Margin = new Thickness(0, 0, 0, 7) }; var roomHint = Caption("01 固定为审讯室。其余15格须明确类型；阶级0–4，绮彩入库后更新捕获数。"); roomHint.FontSize = 11; help.Children.Add(roomHint);
        help.Children.Add(Styled(Button("我已逐格确认：其余未知格均未开启", () => { foreach (var room in rooms.Where(room => room.Index > 0 && room.Kind == "unknown")) room.Kind = "locked"; }), "Talos.GhostButton"));
        DockPanel.SetDock(help, Dock.Top); left.Children.Add(help);
        board.ItemsSource = rooms; ScrollViewer.SetHorizontalScrollBarVisibility(board, ScrollBarVisibility.Auto); ScrollViewer.SetVerticalScrollBarVisibility(board, ScrollBarVisibility.Auto);
        board.Columns.Add(new DataGridTextColumn { Header = "房号", Binding = new Binding("Number"), IsReadOnly = true, Width = 45, ElementStyle = (Style)FindResource("Talos.DataGridTextDisplay") });
        AddCheckColumn("可去", "Destination", 44);
        AddComboColumn("类型", "Kind", Choices(EngineCatalog.Kinds), 90);
        AddComboColumn("种族", "Species", Choices(EngineCatalog.Species, true), 85);
        board.Columns.Add(new DataGridComboBoxColumn { Header = "阶级", ItemsSource = Enumerable.Range(0, 5).Select(index => new { Id = index, Name = EngineCatalog.Tiers[index] }).ToArray(), DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValueBinding = EditBinding("Tier"), Width = 84, ElementStyle = (Style)FindResource("Talos.DataGridComboDisplay"), EditingElementStyle = (Style)FindResource("Talos.DataGridComboEdit") });
        AddCheckColumn("事件", "Event", 45);
        board.Columns.Add(new DataGridCheckBoxColumn { Header = new TextBlock { Text = "已探明", ToolTip = "仅表示已巡过或已确认事件标记有无，不代表已知囚犯或稀有度。勾选“已探明”且未勾选“事件”表示确定无事件；未探明门的潜在事件按模型5%练习概率采样。截图不会自动勾选。" }, Binding = EditBinding("Seen"), Width = 65, ElementStyle = (Style)FindResource("Talos.DataGridCheckBoxDisplay"), EditingElementStyle = (Style)FindResource("Talos.DataGridCheckBoxEdit") });
        AddComboColumn("已揭示事件类型", "EventKind", Choices(EngineCatalog.Events, true), 130);
        board.Columns.Add(new DataGridTextColumn { Header = "孵化日", Binding = EditBinding("NestDue"), Width = 66, ElementStyle = (Style)FindResource("Talos.DataGridTextDisplay"), EditingElementStyle = (Style)FindResource("Talos.DataGridTextEdit") });
        AddCheckColumn("象牙", "Ivory", 45);
        AddCheckColumn("抓痕", "Scratch", 45);
        board.Columns.Add(new DataGridTextColumn { Header = "识别提示（仍需核对）", Binding = new Binding("Note"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 150, ElementStyle = (Style)FindResource("Talos.DataGridTextDisplay") });
        left.Children.Add(board); layout.Children.Add(left);
        var controls = new StackPanel { Margin = new Thickness(0, 0, 0, 4) }; var controlTitle = Caption("本局控制信息", "Talos.Label"); controlTitle.FontSize = 14; controls.Children.Add(controlTitle);
        void Field(string label, FrameworkElement field) { var caption = Caption(label); caption.FontSize = 11; caption.Margin = new Thickness(1, 10, 0, 4); controls.Children.Add(caption); controls.Children.Add(field); }
        Field("当前第几天（1–20，必须手动确认）", day);
        SetItems(position, Enumerable.Range(0, 16).Select(index => new Choice(index.ToString(), $"{index + 1:00} 号"))); Field("典狱长当前位置", position);
        SetItems(phase, Choices(EngineCatalog.Phases)); Field("当前操作阶段", phase);
        Field("已捕获绮彩数", vault); SetItems(objective, Choices(EngineCatalog.Objectives)); Field("左上角任务", objective); Field("任务进度", progress);
        SetItems(usurp, new[] { new Choice("0", "未确认"), new Choice("1", "尚未触发其一"), new Choice("2", "下一次其二"), new Choice("3", "下一次其三"), new Choice("4", "下一次其四"), new Choice("5", "其四已完成") }); Field("僭越进度", usurp);
        SetItems(currentEvent, Choices(EngineCatalog.Events, true)); Field("当前事件（进入后必须明确）", currentEvent);
        SetItems(resume, Choices(EngineCatalog.Phases, true)); Field("奖励选完后实际返回阶段", resume); Field("本次连吃已经吃了几口", chainConsumed);
        foreach (var check in new[] { milestone, used, chain, noSwap, noDevour, unsupported }) { if (check == unsupported) check.Content = new TextBlock { Text = (string)check.Content, TextWrapping = TextWrapping.Wrap }; controls.Children.Add(check); check.Click += (_, _) => Invalidate("控制信息已修改，请重新核对和计算。"); }
        foreach (var text in new[] { day, vault, progress, chainConsumed }) text.TextChanged += (_, _) => { if (!suppress) Invalidate("控制信息已修改，旧建议已清除。"); };
        foreach (var combo in new[] { position, phase, objective, usurp, currentEvent, resume }) combo.SelectionChanged += (_, _) => { if (!suppress) Invalidate("阶段或控制信息已修改，请重新核对。"); };
        var scroller = new ScrollViewer { Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; var controlCard = new Border { Child = scroller, Padding = new Thickness(12), CornerRadius = new CornerRadius(10), Background = ColorOf("Talos.Raised") }; Grid.SetColumn(controlCard, 1); layout.Children.Add(controlCard); return layout;
    }

    private static Binding EditBinding(string path) => new(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true };
    private void AddCheckColumn(string title, string field, double width) => board.Columns.Add(new DataGridCheckBoxColumn { Header = title, Binding = EditBinding(field), Width = width, ElementStyle = (Style)FindResource("Talos.DataGridCheckBoxDisplay"), EditingElementStyle = (Style)FindResource("Talos.DataGridCheckBoxEdit") });
    private void AddComboColumn(string title, string field, Choice[] values, double width) => board.Columns.Add(new DataGridComboBoxColumn { Header = title, ItemsSource = values, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValueBinding = EditBinding(field), Width = width, ElementStyle = (Style)FindResource("Talos.DataGridComboDisplay"), EditingElementStyle = (Style)FindResource("Talos.DataGridComboEdit") });

    private UIElement BuildTools()
    {
        var grid = new Grid { Margin = new Thickness(5, 9, 5, 5) }; grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        UIElement Column(string title, string description, ListBox list)
        {
            var panel = new DockPanel(); var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 10) }; var titleText = Caption(title, "Talos.Label"); titleText.FontSize = 15; heading.Children.Add(titleText); var hint = Caption(description); hint.Margin = new Thickness(0, 5, 0, 0); hint.FontSize = 11; heading.Children.Add(hint); DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading); panel.Children.Add(list); return panel;
        }
        tools.ItemsSource = Choices(EngineCatalog.Tools); tools.DisplayMemberPath = "Name";
        offers.ItemsSource = EngineCatalog.Tools.Select(item => new Choice(item.Key, "刑具 · " + item.Value)).Concat(EngineCatalog.Gifts.Select(item => new Choice(item.Key, "珍藏 · " + item.Value))).ToArray(); offers.DisplayMemberPath = "Name";
        tools.SelectionChanged += (_, _) => { if (!suppress) Invalidate("持有刑具已修改，请核对完整列表。"); }; offers.SelectionChanged += (_, _) => { if (!suppress) Invalidate("奖励候选已修改，请核对实际弹窗选项。"); };
        var left = new Border { Child = Column("已持有刑具", "可多选。只列当前支持的机制；列表外请在第二步勾选“不支持”。", tools), Padding = new Thickness(12), Background = ColorOf("Talos.Raised"), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 6, 0) }; grid.Children.Add(left);
        var right = new Border { Child = Column("本次奖励候选", "只勾选当前刑具 / 珍藏弹窗的实际选项，不要默认全选。", offers), Padding = new Thickness(12), Background = ColorOf("Talos.Raised"), CornerRadius = new CornerRadius(10), Margin = new Thickness(6, 0, 0, 0) }; Grid.SetColumn(right, 1); grid.Children.Add(right); return grid;
    }

    private void LoadState(GameState state, bool blankDay = false)
    {
        Invalidate("局面已加载，所有信息仍需人工确认。"); suppress = true;
        try
        {
            rooms.Clear(); foreach (var cell in state.Rooms.OrderBy(room => room.Index)) { var item = new RoomEditor(cell.Index) { Kind = cell.Kind, Species = cell.Species ?? "", Tier = cell.Tier, NestDue = cell.NestDue, Event = cell.Event, Seen = cell.Seen, EventKind = cell.EventKind ?? "", Ivory = cell.Ivory, Destination = state.Destinations.Contains(cell.Index), Note = source == "example" ? "示例数据，非游戏实况" : "待人工核对" }; item.PropertyChanged += (_, _) => { if (!suppress) Invalidate("房间或目的地已修改，旧建议已清除。"); }; rooms.Add(item); }
            day.Text = blankDay ? "" : state.Day.ToString(); position.SelectedValue = blankDay ? null : state.Position.ToString(); phase.SelectedValue = state.Phase; vault.Text = state.Vault.ToString(); progress.Text = state.ObjectiveProgress.ToString(); objective.SelectedValue = state.Objective;
            usurp.SelectedValue = blankDay ? "0" : state.UsurpStage.ToString(); currentEvent.SelectedValue = state.EventKind ?? ""; resume.SelectedValue = state.ResumePhase ?? "";
            chainConsumed.Text = state.ChainConsumed == 0 ? "" : state.ChainConsumed.ToString(); milestone.IsChecked = state.Milestone; used.IsChecked = state.UsedAction; chain.IsChecked = state.ChainArmed; noSwap.IsChecked = state.NoSwap; noDevour.IsChecked = state.NoDevour; unsupported.IsChecked = state.UnsupportedPresent || state.Rooms.Any(room => room.KnownTier is not null || room.KnownSpecies is not null || room.KnownMinimumTier is not null);
            tools.UnselectAll(); foreach (Choice choice in tools.Items) if (state.Tools.Contains(choice.Id)) tools.SelectedItems.Add(choice);
            offers.UnselectAll(); foreach (Choice choice in offers.Items) if (state.OfferIds.Contains(choice.Id)) offers.SelectedItems.Add(choice);
        }
        finally { suppress = false; confirmed.IsChecked = false; SourceChanged(); }
    }

    private void SourceChanged() => sourceLabel.Text = source == "example" ? "来源：示例研究 · 非游戏实况" : source == "screenshot" ? "来源：截图预填 · 未经人工核对不能计算" : "来源：手工录入 · 当前无游戏截图";
    private void Invalidate(string message, bool cancelWork = true)
    {
        if (suppress) return; revision++; if (cancelWork) work?.Cancel(); confirmed.IsChecked = false;
        output.Text = message + "\n结果来自有限规则和采样，不保证全局最优。"; SetAssistantState("待核对", message); status.Text = message;
    }
    private string assistantState = "等待截图";
    private void SetAssistantState(string state, string message) { assistantState = state; overlaySummary = message; overlay?.SetState(state, message); }
    private void CancelCurrentOperation()
    {
        StopFollowing("已取消操作，日志跟随已停止。", false); captureWork?.Cancel(); Invalidate("已取消当前操作，旧建议不再适用。"); SetAssistantState("已取消", "请读取新画面或重新核对局面。"); SetBusy(false);
    }
    private void CompleteWork(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(work, cancellation)) { work = null; SetBusy(false); }
        cancellation.Dispose();
    }
    private void SetBusy(bool value) { if (closed) return; advise.IsEnabled = !value && !captureBusy; recognize.IsEnabled = !value && !captureBusy; capture.IsEnabled = !captureBusy; }
    private nint WindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled) { if (message == NativeCapture.WmHotkey && wParam.ToInt32() == NativeCapture.HotkeyId) { handled = true; _ = CaptureAsync(); } return 0; }
    private void ResetSource() { StopFollowing("截图来源已切换，日志跟随已停止。", false); captureWork?.Cancel(); ClearImage(); source = "manual"; LoadState(GameAdvisor.CreateEmpty(), true); status.Text = "截图来源已切换，旧局面与建议已清空。请读取新画面。"; SetAssistantState("等待截图", status.Text); }
    private void RefreshSources() { var previous = sources.SelectedItem as CaptureTarget; var list = NativeCapture.Sources(); refreshingSources = true; try { sources.ItemsSource = list; sources.SelectedItem = list.FirstOrDefault(item => item.Handle == previous?.Handle && item.ProcessId == previous.ProcessId) ?? list.Last(); } finally { refreshingSources = false; } if (previous is not null && (previous.Handle != (sources.SelectedItem as CaptureTarget)?.Handle || previous.ProcessId != (sources.SelectedItem as CaptureTarget)?.ProcessId)) ResetSource(); }

    public async Task CaptureAsync()
    {
        if (closed || closing) return;
        StopFollowing("已切换到手动读取，日志跟随已停止。", false);
        await CaptureSnapshotAsync(false, CancellationToken.None);
    }
    private async Task<bool> CaptureSnapshotAsync(bool automatic, CancellationToken sessionToken)
    {
        if (captureBusy || closed || closing) return false; captureBusy = true; Invalidate("正在获取新画面，旧建议已清除。"); long epoch = revision; BitmapSource? addedImage = null; SetBusy(true);
        var cancellation = captureWork = CancellationTokenSource.CreateLinkedTokenSource(sessionToken); var token = cancellation.Token;
        bool wasVisible = IsVisible, overlayVisible = overlay?.IsVisible == true; var oldState = WindowState;
        var visibleUpdate = updateWindow?.IsVisible == true ? updateWindow : null;
        try
        {
            var target = sources.SelectedItem as CaptureTarget ?? new CaptureTarget(0, "当前屏幕");
            if (automatic && !NativeCapture.CanAutoCapture(target, out string reason)) throw new InvalidOperationException(reason);
            SetAssistantState("等待截图", automatic ? "日志已稳定，正在读取前台游戏窗口。" : "正在读取新画面，旧建议已失效。");
            visibleUpdate?.Hide(); Hide(); overlay?.Hide();
            await Task.Delay(180, token);
            var bitmap = await Task.Run(() => { token.ThrowIfCancellationRequested(); return automatic ? NativeCapture.CaptureGame(target, handle) : NativeCapture.Capture(target, handle); }, token);
            token.ThrowIfCancellationRequested(); if (!closed && !closing && epoch == revision) { SetScreenshot(bitmap); addedImage = bitmap; }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed && !closing && epoch == revision) { status.Text = "截图未完成：" + error.Message + " 旧建议不会恢复。"; SetAssistantState("等待截图", status.Text); } }
        finally
        {
            if (!closed && !closing)
            {
                if (wasVisible && !compactMode) { bool activate = ShowActivated; ShowActivated = false; Show(); WindowState = oldState; ShowActivated = activate; }
                if (overlayVisible) overlay?.Show();
                if (!compactMode && visibleUpdate is not null && ReferenceEquals(updateWindow, visibleUpdate)) { bool activate = visibleUpdate.ShowActivated; visibleUpdate.ShowActivated = false; visibleUpdate.Show(); visibleUpdate.ShowActivated = activate; }
            }
            captureBusy = false;
            if (ReferenceEquals(captureWork, cancellation)) captureWork = null;
            cancellation.Dispose();
            if (!closed && !closing)
            {
                SetBusy(false);
                if (pendingUpdateReturn is { } pending) { pendingUpdateReturn = null; ReturnFromUpdates(pending.State, pending.Generation); }
            }
        }
        if (!closed && !closing && !sessionToken.IsCancellationRequested && addedImage is not null && ReferenceEquals(currentImage, addedImage) && calibration.LayoutConfirmed) await RecognizeAsync(sessionToken);
        return !closed && !closing && !sessionToken.IsCancellationRequested && addedImage is not null && ReferenceEquals(currentImage, addedImage);
    }

    private static BitmapSource ReadImage(string file)
    {
        if (new FileInfo(file).Length > 40 * 1024 * 1024) throw new InvalidOperationException("图片超过40 MB，请先缩小。");
        using var stream = File.OpenRead(file); var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        if ((long)image.PixelWidth * image.PixelHeight > 24_000_000) throw new InvalidOperationException("图片超过2400万像素，请先缩小。"); return image;
    }
    private async Task ImportAsync() { StopFollowing("已切换到导入图片，日志跟随已停止。"); captureWork?.Cancel(); var dialog = new OpenFileDialog { Filter = "游戏截图|*.png;*.jpg;*.jpeg;*.bmp" }; if (dialog.ShowDialog(this) != true) return; Invalidate("正在读取新图片，旧建议已清除。"); long epoch = revision; try { var image = await Task.Run(() => ReadImage(dialog.FileName)); if (closed || closing || epoch != revision) return; SetScreenshot(image); if (calibration.LayoutConfirmed) await RecognizeAsync(); } catch (Exception error) { if (!closed && !closing && epoch == revision) status.Text = "导入失败：" + error.Message + " 旧建议不再显示。"; } }
    private async Task PasteAsync() { StopFollowing("已切换到粘贴图片，日志跟随已停止。"); captureWork?.Cancel(); Invalidate("正在读取剪贴板图片，旧建议已清除。"); try { var image = Clipboard.GetImage(); if (image is null) { status.Text = "剪贴板中没有图片。"; return; } image.Freeze(); SetScreenshot(image); if (calibration.LayoutConfirmed) await RecognizeAsync(); } catch (Exception error) { if (!closed && !closing) status.Text = "粘贴失败：" + error.Message; } }
    private void ClearImage() { currentImage = null; preview.Source = null; previewEmpty.Visibility = Visibility.Visible; marks.Children.Clear(); }
    private void SetScreenshot(BitmapSource image)
    {
        Invalidate("已获取新截图；请核对校准、牢房及上次保留的控制信息。"); currentImage = image; preview.Source = image; previewEmpty.Visibility = Visibility.Collapsed; source = "screenshot"; SourceChanged();
        suppress = true; try { foreach (var room in rooms) { room.Kind = room.Index == 0 ? "office" : "unknown"; room.Species = ""; room.Tier = null; room.Event = false; room.Seen = false; room.EventKind = ""; room.Ivory = false; room.Scratch = false; room.NestDue = null; room.Destination = false; room.Note = "新截图，待核对"; } } finally { suppress = false; }
        DrawCalibration(); status.Text = calibration.LayoutConfirmed ? "截图已保留，准备离线预填；全部结果需手动确认。" : "首次请框选区域，逐格标定16个中心；也可直接手工录入。";
        SetAssistantState("待核对", "新截图已到达。请展开核对牢房、天数、位置和控制信息，再计算建议。");
    }

    private static PixelImage Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); converted.Freeze(); int stride = converted.PixelWidth * 4; var bytes = new byte[checked(stride * converted.PixelHeight)]; converted.CopyPixels(bytes, stride, 0);
        for (int i = 0; i < bytes.Length; i += 4) (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]); return new PixelImage(converted.PixelWidth, converted.PixelHeight, bytes);
    }
    private ScreenshotRecognizer LoadRecognizer()
    {
        string folder = System.IO.Path.Combine(context.VersionRoot, "assets"); if (!File.Exists(System.IO.Path.Combine(folder, "templates.json"))) folder = System.IO.Path.Combine(context.VersionRoot, "templates");
        using var json = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(folder, "templates.json"))); var templates = new List<VisionTemplate>();
        foreach (var face in json.RootElement.GetProperty("faces").EnumerateArray()) { string? file = face.GetProperty("file").GetString(), label = face.GetProperty("label").GetString(); if (file is null || label is null || System.IO.Path.GetFileName(file) != file || !file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue; templates.Add(new VisionTemplate("faces", label, Pixels(ReadImage(System.IO.Path.Combine(folder, file))))); }
        return new ScreenshotRecognizer(templates);
    }
    private async Task RecognizeAsync(CancellationToken sessionToken = default)
    {
        if (currentImage is null) { status.Text = "请先获取图片。"; return; } if (!calibration.LayoutConfirmed) { status.Text = "请先确认16个中心点位。"; return; }
        Invalidate("正在离线预填，旧建议已清除。"); var epoch = revision; var image = currentImage; var settings = calibration; var cancellation = work = CancellationTokenSource.CreateLinkedTokenSource(sessionToken); var token = cancellation.Token; SetBusy(true); SetAssistantState("识别中", "本机识别正在预填候选；识别完成后仍需人工核对。");
        try
        {
            var result = await Task.Run(() => { token.ThrowIfCancellationRequested(); recognizer ??= LoadRecognizer(); return recognizer.Recognize(Pixels(image), settings); }, token);
            if (closed || epoch != revision || cancellation.IsCancellationRequested) return;
            suppress = true; try { foreach (var item in result.Rooms) { var room = rooms[item.Index]; room.Note = item.Message; if (item.Index == 0) continue; room.Species = item.Species ?? ""; room.Tier = item.Tier; room.Kind = !item.Unknown && item.Species is not null && item.Tier is not null ? "inmate" : "unknown"; } } finally { suppress = false; }
            status.Text = string.Join("\n", result.Issues); confirmed.IsChecked = false; output.Text = "识图仅作预填。请到第二、三页补齐并核对所有控制信息，再明确勾选确认。";
            SetAssistantState("待核对", "截图已预填。请展开助手，逐格核对和补齐控制信息；不会自动确认或给出建议。");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed && epoch == revision) { status.Text = "离线识别未完成：" + error.Message + " 可手工录入；不恢复旧建议。"; SetAssistantState("待核对", status.Text); } }
        finally { CompleteWork(cancellation); }
    }

    private GameState Collect()
    {
        if (!board.CommitEdit(DataGridEditingUnit.Cell, true) || !board.CommitEdit(DataGridEditingUnit.Row, true) || HasValidationError(board)) throw new InvalidOperationException("牢房表格存在无效输入，请先修正红色标记（例如孵化日必须为整数）。");
        int Read(TextBox field, string label, bool optional = false) { if (optional && string.IsNullOrWhiteSpace(field.Text)) return 0; if (!int.TryParse(field.Text, out int value)) throw new InvalidOperationException("请手动填写" + label + "整数。"); return value; }
        if (!int.TryParse(Selected(position), out int current)) throw new InvalidOperationException("请确认典狱长当前位置。");
        return new GameState { Day = Read(day, "当前第几天"), Position = current, Phase = Selected(phase), Rooms = rooms.Select(room => room.ToState()).ToList(), Destinations = rooms.Where(room => room.Destination).Select(room => room.Index).ToList(), Tools = tools.SelectedItems.Cast<Choice>().Select(item => item.Id).ToList(), Vault = Read(vault, "已捕获绮彩数"), Objective = Selected(objective), ObjectiveProgress = Read(progress, "任务进度"), Milestone = milestone.IsChecked == true, UsedAction = used.IsChecked == true, NoSwap = noSwap.IsChecked == true, NoDevour = noDevour.IsChecked == true, ChainArmed = chain.IsChecked == true, ChainConsumed = Read(chainConsumed, "已连吃口数", true), UsurpStage = int.TryParse(Selected(usurp), out int stage) ? stage : 0, EventKind = string.IsNullOrEmpty(Selected(currentEvent)) ? null : Selected(currentEvent), OfferIds = offers.SelectedItems.Cast<Choice>().Select(item => item.Id).ToList(), ResumePhase = string.IsNullOrEmpty(Selected(resume)) ? null : Selected(resume), Confirmed = confirmed.IsChecked == true, UnsupportedPresent = unsupported.IsChecked == true || rooms.Any(room => room.Scratch), Source = source };
    }
    private static bool HasValidationError(DependencyObject root) { if (Validation.GetHasError(root)) return true; for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) if (HasValidationError(VisualTreeHelper.GetChild(root, index))) return true; return false; }
    private void OpenDataDirectory()
    {
        try { Process.Start(new ProcessStartInfo(context.DataRoot) { UseShellExecute = true }); }
        catch (Exception error) { status.Text = "无法打开数据目录：" + error.Message + " 可展开“使用说明与数据目录”复制路径，在文件资源管理器中打开。"; }
    }
    private void ShowUpdates()
    {
        if (closed || closing || Dispatcher.HasShutdownStarted) return;
        if (captureBusy) { status.Text = "正在截图，请稍后打开更新窗口。"; return; }
        if (updateWindow is not null) { if (!updateWindow.IsVisible) updateWindow.Show(); updateWindow.Activate(); return; }
        var returnState = WindowState == WindowState.Minimized ? lastNonMinimizedState : WindowState;
        lastNonMinimizedState = returnState; pendingUpdateReturn = null;
        long generation = ++updateWindowGeneration;
        var window = new UpdateWindow(context) { Owner = this }; updateWindow = window;
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(updateWindow, window)) return;
            updateWindow = null;
            ReturnFromUpdates(returnState, generation);
        };
        try { window.Show(); }
        catch (Exception error) { if (ReferenceEquals(updateWindow, window)) updateWindow = null; status.Text = "无法打开更新窗口：" + error.Message; }
    }
    private void ReturnFromUpdates(WindowState state, long generation)
    {
        if (closed || closing || compactMode || generation != updateWindowGeneration || updateWindow is not null || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (captureBusy) { pendingUpdateReturn = (state, generation); return; }
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (closed || closing || compactMode || generation != updateWindowGeneration || updateWindow is not null || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (captureBusy) { pendingUpdateReturn = (state, generation); return; }
            if (!IsVisible) Show();
            WindowState = state; Activate();
        }));
    }
    private void ShowCompact()
    {
        if (closed || closing || captureBusy) return;
        compactMode = true;
        overlay ??= new OverlayWindow(() => { if (closed || closing || captureBusy) return; compactMode = false; Show(); if (WindowState == WindowState.Minimized) WindowState = lastNonMinimizedState; Activate(); }, () => _ = CaptureAsync());
        overlay.SetState(assistantState, overlaySummary ?? "先读取并完整核对局面，再计算建议。\nF8可读取新画面。"); overlay.Show(); updateWindow?.Hide(); Hide();
    }

    private Rect DisplayRect()
    {
        if (currentImage is null) return Rect.Empty; double scale = Math.Min(marks.ActualWidth / currentImage.PixelWidth, marks.ActualHeight / currentImage.PixelHeight); double width = currentImage.PixelWidth * scale, height = currentImage.PixelHeight * scale; return new Rect((marks.ActualWidth - width) / 2, (marks.ActualHeight - height) / 2, width, height);
    }
    private NormalizedPoint? ImagePoint(Point point) { var rect = DisplayRect(); return rect.IsEmpty || !rect.Contains(point) ? null : new NormalizedPoint((point.X - rect.X) / rect.Width, (point.Y - rect.Y) / rect.Height); }
    private void PreviewDown(object sender, MouseButtonEventArgs args)
    {
        var point = ImagePoint(args.GetPosition(marks)); if (point is null) return;
        if (calibrationMode == "roi") { dragStart = point; marks.CaptureMouse(); }
        else if (calibrationMode == "points" && clickedCenters.Count < 16) { var roi = calibration.Roi; double x = (point.Value.X - roi.X) / roi.Width, y = (point.Value.Y - roi.Y) / roi.Height; if (x is < 0 or > 1 || y is < 0 or > 1) { status.Text = "请点击已选区域内的房间中心。"; return; } clickedCenters.Add(new NormalizedPoint(x, y)); DrawCalibration(); status.Text = clickedCenters.Count == 16 ? "16个位置已标定，请核对后勾选点位确认并保存。" : $"请点击{clickedCenters.Count + 1:00}号中心（顺时针）。"; if (clickedCenters.Count == 16) { calibration = calibration with { RoomCenters = clickedCenters.ToArray(), LayoutConfirmed = false }; } }
    }
    private void PreviewMove(object sender, MouseEventArgs args) { if (dragStart is not null && calibrationMode == "roi") { var point = ImagePoint(args.GetPosition(marks)); if (point is not null) DrawCalibration(new NormalizedRect(Math.Min(point.Value.X, dragStart.Value.X), Math.Min(point.Value.Y, dragStart.Value.Y), Math.Abs(point.Value.X - dragStart.Value.X), Math.Abs(point.Value.Y - dragStart.Value.Y))); } }
    private void PreviewUp(object sender, MouseButtonEventArgs args)
    {
        if (dragStart is null) return; var point = ImagePoint(args.GetPosition(marks)); marks.ReleaseMouseCapture(); if (point is not null) { var start = dragStart.Value; var roi = new NormalizedRect(Math.Min(point.Value.X, start.X), Math.Min(point.Value.Y, start.Y), Math.Abs(point.Value.X - start.X), Math.Abs(point.Value.Y - start.Y)); if (roi.Width > .1 && roi.Height > .1) { calibration = VisionCalibration.CreateLegacyLayout(roi); layoutConfirmed.IsChecked = false; Invalidate("区域已更新，请重新确认或标定16个中心。", true); } } dragStart = null; calibrationMode = ""; DrawCalibration();
    }
    private void DrawCalibration(NormalizedRect? draft = null)
    {
        marks.Children.Clear(); var rect = DisplayRect(); if (rect.IsEmpty || rect.Width <= 0) return; var roi = draft ?? calibration.Roi;
        var box = new Rectangle { Width = roi.Width * rect.Width, Height = roi.Height * rect.Height, Stroke = ColorOf("Talos.Gold"), StrokeThickness = 2, IsHitTestVisible = false }; Canvas.SetLeft(box, rect.X + roi.X * rect.Width); Canvas.SetTop(box, rect.Y + roi.Y * rect.Height); marks.Children.Add(box);
        var points = calibrationMode == "points" ? clickedCenters : calibration.RoomCenters;
        for (int index = 0; index < points.Count; index++) { var point = points[index]; var label = new TextBlock { Text = (index + 1).ToString("00"), Background = ColorOf("Talos.Gold"), Foreground = ColorOf("Talos.Background"), FontWeight = FontWeights.Bold, Padding = new Thickness(3), IsHitTestVisible = false }; Canvas.SetLeft(label, rect.X + (roi.X + point.X * roi.Width) * rect.Width - 11); Canvas.SetTop(label, rect.Y + (roi.Y + point.Y * roi.Height) * rect.Height - 11); marks.Children.Add(label); }
    }
    private void SaveCalibration()
    {
        if (currentImage is null || calibration.RoomCenters.Count != 16 || layoutConfirmed.IsChecked != true) { status.Text = "请先获取截图，确认完整16个中心，再保存。"; return; }
        try { calibration = calibration with { LayoutConfirmed = true, ReferenceWidth = currentImage.PixelWidth, ReferenceHeight = currentImage.PixelHeight }; Directory.CreateDirectory(context.DataRoot); string path = System.IO.Path.Combine(context.DataRoot, "vision-calibration.json"); File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(calibration)); File.Move(path + ".tmp", path, true); status.Text = "校准已在本机保存；请离线识别并核对本张截图。"; } catch (Exception error) { status.Text = "校准未能保存：" + error.Message; }
    }
    private void LoadCalibration() { try { var path = System.IO.Path.Combine(context.DataRoot, "vision-calibration.json"); if (!File.Exists(path)) return; var saved = JsonSerializer.Deserialize<VisionCalibration>(File.ReadAllText(path)); if (saved is not null && saved.RoomCenters.Count == 16) { calibration = saved; suppress = true; layoutConfirmed.IsChecked = saved.LayoutConfirmed; suppress = false; } } catch (Exception error) { status.Text = "本机校准未能读取，请重新标定：" + error.Message; } }
}

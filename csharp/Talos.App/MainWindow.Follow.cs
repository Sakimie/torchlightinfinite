using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Talos.Core;
using Talos.Core.Following;

namespace Talos.App;

public sealed partial class MainWindow
{
    private sealed record FollowPreferences
    {
        public string Path { get; init; } = "";
        public int SettleMilliseconds { get; init; } = 1500;
        public int MaximumDelayMilliseconds { get; init; } = 8000;
        public string[] CustomKeywords { get; init; } = Array.Empty<string>();
        public LogFollowSettings ToSettings() => new() { Enabled = true, SettleMilliseconds = SettleMilliseconds, MaximumDelayMilliseconds = MaximumDelayMilliseconds, CustomKeywords = CustomKeywords };
    }

    private readonly LogFollowService followService = new();
    private readonly CheckBox followToggle = new() { Content = "日志跟随 · 关闭", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0), FontSize = 11 };
    private readonly DispatcherTimer followTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private FollowPreferences followPreferences = new();
    private CancellationTokenSource? followWork;
    private LogTrigger? readyFollowTrigger;
    private long followEpoch;
    private bool followPolling, updatingFollowToggle, followWasEligible, followPauseDisplayed, followDisposed;
    private LogFollowState previousFollowState = LogFollowState.Disabled;
    private bool HasFollowPending => followWork is not null && (readyFollowTrigger is not null || followService.Status.HasPending);

    private UIElement BuildFollowControls()
    {
        var controls = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        followToggle.Checked += (_, _) => { if (!updatingFollowToggle) StartFollowing(); };
        followToggle.Unchecked += (_, _) => { if (!updatingFollowToggle) StopFollowing("日志跟随已停止，旧建议已清除。"); };
        controls.Children.Add(followToggle); controls.Children.Add(Styled(Button("跟随设置", ConfigureFollowing), "Talos.GhostButton")); return controls;
    }

    private void InitializeFollowing()
    {
        try
        {
            string path = System.IO.Path.Combine(context.DataRoot, "log-follow.json");
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 65536) throw new InvalidOperationException("设置文件过大。");
                followPreferences = JsonSerializer.Deserialize<FollowPreferences>(File.ReadAllText(path)) ?? new();
                followPreferences.ToSettings().Validate();
            }
        }
        catch (Exception error) { followPreferences = new(); status.Text = "日志跟随设置未读取：" + error.Message + " 请重新选择日志。"; }
        SetFollowHint("关闭", "每次启动默认关闭。请先选择本地游戏日志和明确的游戏窗口，再主动开启。");
        followTimer.Tick += FollowTick;
    }

    private void StartFollowing()
    {
        if (closed || closing || followDisposed) return;
        if (string.IsNullOrWhiteSpace(followPreferences.Path)) { SetFollowToggle(false); status.Text = "请先点击“跟随设置”选择本地.log游戏日志，再主动开启。"; return; }
        if (sources.SelectedItem is not CaptureTarget target || target.Handle == 0) { SetFollowToggle(false); status.Text = "日志跟随只允许明确的游戏窗口，不能选择全屏来源。请先选择游戏窗口。"; return; }
        StopFollowing("正在重新连接日志。", false);
        try
        {
            followService.Start(followPreferences.Path, followPreferences.ToSettings());
            followWork = new CancellationTokenSource(); ++followEpoch; readyFollowTrigger = null; followWasEligible = false; followPauseDisplayed = false; previousFollowState = LogFollowState.Disabled;
            SetFollowToggle(true); Invalidate("日志跟随已开启，从当前位置追读；旧建议已清除，新截图仍须人工核对。");
            SetAssistantState("等待日志", "只在已选游戏窗口位于前台时截图。请切回游戏；未命中日志时仍可手动 F8。");
            SetFollowHint("等待", "已连接 " + System.IO.Path.GetFileName(followPreferences.Path) + "。窗口在后台或最小化时暂停；截图后不会自动确认或计算。"); followTimer.Start();
        }
        catch (Exception error) { followService.Stop(); SetFollowToggle(false); status.Text = "无法开启日志跟随：" + error.Message; SetFollowHint("关闭", status.Text); }
    }

    private void StopFollowing(string reason, bool invalidate = true)
    {
        var old = followWork; bool wasActive = old is not null || readyFollowTrigger is not null;
        followWork = null; ++followEpoch; readyFollowTrigger = null; followTimer.Stop(); followService.Stop(); followWasEligible = false;
        if (old is not null) { old.Cancel(); if (!followPolling) old.Dispose(); }
        if (wasActive) { captureWork?.Cancel(); work?.Cancel(); }
        if (!closed && !closing)
        {
            SetFollowToggle(false);
            SetFollowHint("关闭", reason);
            if (wasActive && invalidate) { Invalidate(reason); SetAssistantState("跟随已停止", reason + "\n可使用 F8、导入或手动核对继续。"); }
        }
    }

    private void SetFollowToggle(bool enabled) { updatingFollowToggle = true; try { followToggle.IsChecked = enabled; } finally { updatingFollowToggle = false; } }
    private void SetFollowHint(string label, string detail)
    {
        if (closed || closing) return;
        followToggle.Content = "日志跟随 · " + label; followToggle.ToolTip = detail;
        followToggle.SetResourceReference(ForegroundProperty, followWork is null ? "Talos.Muted" : "Talos.Accent");
    }
    private bool IsCurrentFollow(CancellationTokenSource session, long epoch) => !closed && !closing && epoch == followEpoch && ReferenceEquals(followWork, session) && !session.IsCancellationRequested;

    private async void FollowTick(object? sender, EventArgs args)
    {
        var session = followWork; if (followPolling || session is null || closed || closing) return;
        followPolling = true; long epoch = followEpoch; var token = session.Token;
        try
        {
            var result = await followService.PollAsync(token);
            if (!IsCurrentFollow(session, epoch)) return;
            if (result.HasNewMatch || result.RequiresReview)
            {
                readyFollowTrigger = null; captureWork?.Cancel(); Invalidate(result.HasNewMatch ? "检测到典狱日志变化，旧建议已清除；正在等待动画稳定。" : "日志被截断、替换、丢失或出现读取缺口，旧建议已失效；请重新核对画面。");
                SetAssistantState("等待截图", "日志只是截图提示，尚未更新局面。等待稳定事件或手动 F8 后，仍须人工核对。");
            }
            if (result.PendingExpired)
            {
                readyFollowTrigger = null; SetFollowHint("等待", "典狱事件持续变化，已放弃本次截图，等待下一次稳定事件。");
                SetAssistantState("等待截图", "日志持续变化，未自动截图。可等待下一次事件或手动 F8。");
            }
            if (result.Status.State is LogFollowState.Faulted or LogFollowState.WaitingForFile)
            {
                readyFollowTrigger = null;
                if (previousFollowState != result.Status.State || result.HasNewMatch) { Invalidate(result.Status.State == LogFollowState.WaitingForFile ? "所选日志暂不存在，等待游戏生成。" : result.Status.LastError); SetAssistantState("跟随已暂停", status.Text); }
                previousFollowState = result.Status.State; SetFollowHint("暂停", result.Status.LastError.Length > 0 ? result.Status.LastError : "等待所选日志出现。仍可手动 F8。"); return;
            }
            previousFollowState = result.Status.State;
            if (result.Trigger is not null) readyFollowTrigger = result.Trigger;
            var target = sources.SelectedItem as CaptureTarget;
            string pauseReason = "请选择明确的游戏窗口，自动模式不读取全屏。";
            bool eligible = target is not null && NativeCapture.CanAutoCapture(target, out pauseReason);
            if (!eligible)
            {
                if (!followPauseDisplayed || followWasEligible || result.HasNewMatch) { Invalidate("日志跟随已暂停：" + pauseReason); SetAssistantState("跟随已暂停", pauseReason + "\n恢复游戏前台后，才处理待截图提示。"); }
                followWasEligible = false; followPauseDisplayed = true; SetFollowHint("暂停", pauseReason); return;
            }
            followWasEligible = true; followPauseDisplayed = false;
            if (assistantState == "跟随已暂停") SetAssistantState("等待日志", "游戏窗口已恢复前台，等待稳定的典狱日志提示；新图仍须核对。");
            if (readyFollowTrigger is { } trigger && !captureBusy)
            {
                readyFollowTrigger = null;
                if (trigger.IsRunStartHint) { ClearImage(); source = "manual"; LoadState(GameAdvisor.CreateEmpty(), true); }
                SetFollowHint("截图", "日志已稳定，正在读取明确的前台游戏窗口。");
                bool captured = await CaptureSnapshotAsync(true, token);
                if (!IsCurrentFollow(session, epoch)) return;
                if (captured) SetFollowHint("运行", "新截图待人工核对；等待后续日志。不会自动确认或计算。");
                else { SetFollowHint("等待", "本次未取得画面，等待下一次日志，或按 F8 重试。"); SetAssistantState("等待截图", status.Text + "\n本次未取得画面，等待下一次日志，或按 F8 重试。"); }
            }
            else if (result.Status.HasPending) SetFollowHint("等待", $"等待日志静默 {followPreferences.SettleMilliseconds} 毫秒后截图。");
            else if (!result.PendingExpired) SetFollowHint("运行", "只追读新日志。若未命中候选关键字，可手动 F8；真实日志兼容性待实测。");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (IsCurrentFollow(session, epoch)) StopFollowing("日志跟随未完成：" + error.Message); }
        finally { followPolling = false; if (!ReferenceEquals(followWork, session)) session.Dispose(); }
    }

    private void ConfigureFollowing()
    {
        StopFollowing("打开设置已停止日志跟随；保存后需主动开启。");
        var dialog = new Window { Title = "日志跟随设置", Owner = this, Width = 620, Height = 560, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner }; Theme.Apply(dialog);
        var content = new StackPanel { Margin = new Thickness(20) }; var title = Caption("日志只用来提示截图", "Talos.Title"); title.FontSize = 21; content.Children.Add(title);
        var description = Caption("选择本地 .log 游戏日志。每次启动默认关闭；开启后仅读取新增内容，不上传日志或截图。截图结果仍需逐格核对。"); description.Margin = new Thickness(0, 8, 0, 12); content.Children.Add(description);
        var path = new TextBox { Text = followPreferences.Path, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 55, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        content.Children.Add(path); content.Children.Add(Button("选择本地.log日志", () => { var picker = new OpenFileDialog { Title = "选择本地游戏.log日志", Filter = "游戏日志 (*.log)|*.log", CheckFileExists = true }; if (picker.ShowDialog(dialog) == true) path.Text = picker.FileName; }));
        var delays = new Grid { Margin = new Thickness(0, 10, 0, 0) }; delays.ColumnDefinitions.Add(new ColumnDefinition()); delays.ColumnDefinitions.Add(new ColumnDefinition());
        var settle = new TextBox { Text = followPreferences.SettleMilliseconds.ToString() }; var maximum = new TextBox { Text = followPreferences.MaximumDelayMilliseconds.ToString() };
        var delayLeft = new StackPanel { Margin = new Thickness(0, 0, 8, 0) }; delayLeft.Children.Add(Caption("动画静默等待 / 毫秒")); delayLeft.Children.Add(settle); delays.Children.Add(delayLeft);
        var delayRight = new StackPanel { Margin = new Thickness(8, 0, 0, 0) }; delayRight.Children.Add(Caption("连续变化丢弃上限 / 毫秒")); delayRight.Children.Add(maximum); Grid.SetColumn(delayRight, 1); delays.Children.Add(delayRight); content.Children.Add(delays);
        var keywordLabel = Caption("自定义具体关键字（可留空，每行一条）"); keywordLabel.Margin = new Thickness(0, 13, 0, 4); content.Children.Add(keywordLabel);
        var keywords = new TextBox { Text = string.Join("\n", followPreferences.CustomKeywords), AcceptsReturn = true, Height = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; content.Children.Add(keywords);
        var matcherHint = Caption("内置候选：典狱 / 牢房 / Tartarus / S16 标识 + 开局、移动、吞噬、替换、奖励或确认事件。自定义也须带典狱领域标识和具体事件；普通 error / warning 不会触发。实际客户端日志尚待验证，无命中仍可 F8。"); matcherHint.FontSize = 11; matcherHint.Margin = new Thickness(0, 8, 0, 5); content.Children.Add(matcherHint);
        var errorText = Caption(""); errorText.Foreground = ColorOf("Talos.Gold"); content.Children.Add(errorText);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Styled(Button("保存设置", () =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path.Text) || !string.Equals(System.IO.Path.GetExtension(path.Text), ".log", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择本地.log日志文件。");
                if (!int.TryParse(settle.Text, out int wait) || !int.TryParse(maximum.Text, out int limit)) throw new InvalidOperationException("等待时间请填写整数毫秒。");
                var next = new FollowPreferences { Path = System.IO.Path.GetFullPath(path.Text), SettleMilliseconds = wait, MaximumDelayMilliseconds = limit, CustomKeywords = keywords.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).Where(value => value.Length > 0).ToArray() }; next.ToSettings().Validate();
                Directory.CreateDirectory(context.DataRoot); string settingsPath = System.IO.Path.Combine(context.DataRoot, "log-follow.json"); File.WriteAllText(settingsPath + ".tmp", JsonSerializer.Serialize(next)); File.Move(settingsPath + ".tmp", settingsPath, true); followPreferences = next;
                dialog.Close(); status.Text = "跟随设置已在数据目录保存。请选择游戏窗口，再主动开启“日志跟随”。"; SetFollowHint("关闭", status.Text);
            }
            catch (Exception error) { errorText.Text = error.Message; }
        }), "Talos.PrimaryButton")); buttons.Children.Add(Button("返回", dialog.Close)); content.Children.Add(buttons);
        dialog.Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; dialog.ShowDialog();
    }

    private void ShutdownFollowing()
    {
        StopFollowing("助手已关闭。", false); followTimer.Tick -= FollowTick;
        followDisposed = true; _ = DisposeFollowAsync();
    }
    private async Task DisposeFollowAsync() { try { await followService.DisposeAsync().ConfigureAwait(false); } catch (Exception) { } }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Talos.Updates;

namespace Talos.App;

public sealed class UpdateWindow : Window
{
    private readonly LaunchContext context;
    private readonly Lazy<UpdateService> service;
    private readonly TextBlock version = new() { TextWrapping = TextWrapping.Wrap, FontSize = 15, LineHeight = 25 }, status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 19 }, available = new() { Text = "尚未联网检查", FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox notes = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 112, Height = 154, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Padding = new Thickness(12), FontSize = 13 };
    private readonly ProgressBar progress = new() { Height = 4, Margin = new Thickness(0, 0, 0, 12) };
    private readonly Button check = new() { Content = "检查更新" }, install = new() { Content = "下载并安装" }, local = new() { Content = "导入离线更新" }, rollback = new() { Content = "回退旧版" }, restart = new() { Content = "重启并使用" }, cancel = new() { Content = "取消" };
    private ReleaseManifest? manifest;
    private UpdateStatus? current;
    private CancellationTokenSource? work;
    private bool busy, closed, serviceDisposed;
    private long epoch;
    private Task cancellationCallbacks = Task.CompletedTask;

    public UpdateWindow(LaunchContext context)
    {
        this.context = context; service = new Lazy<UpdateService>(() => new UpdateService(context.DataRoot, context.PublicKeyPem, context.FeedUrl));
        Title = $"典狱助手 {context.Version} · 助手更新"; Width = Math.Min(730, Math.Max(460, SystemParameters.WorkArea.Width - 40));
        Height = Math.Min(710, Math.Max(300, SystemParameters.WorkArea.Height - 56));
        MinWidth = 460; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        Theme.Apply(this);

        var root = new Grid { Margin = new Thickness(24, 22, 24, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel();
        title.Children.Add(Caption("助手更新", "Talos.Title"));
        var introduction = Caption("按需更新，随时继续当前助手。", "Talos.Subtitle");
        introduction.Margin = new Thickness(0, 5, 0, 0); title.Children.Add(introduction);
        heading.Children.Add(title);
        var badgeText = Caption("离线优先", "Talos.Label");
        badgeText.SetResourceReference(TextBlock.ForegroundProperty, "Talos.Accent");
        var badge = new Border { Child = badgeText, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 6, 12, 6), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 3, 0, 0) };
        badge.SetResourceReference(Border.BackgroundProperty, "Talos.Raised");
        Grid.SetColumn(badge, 1); heading.Children.Add(badge); root.Children.Add(heading);

        var cards = new StackPanel { Margin = new Thickness(0, 18, 0, 18) };
        var versions = new StackPanel();
        versions.Children.Add(Caption("版本与恢复", "Talos.Label"));
        version.Text = $"当前正在运行：{context.Version}\n已启用：正在读取…";
        version.Margin = new Thickness(0, 10, 0, 0); versions.Children.Add(version);
        var recovery = new WrapPanel { Margin = new Thickness(-3, 12, -3, 0) };
        SetButton(restart, "Talos.SecondaryButton"); SetButton(rollback, "Talos.GhostButton");
        recovery.Children.Add(restart); recovery.Children.Add(rollback); versions.Children.Add(recovery);
        cards.Children.Add(Card(versions));

        var update = new StackPanel();
        var updateHeading = new Grid(); updateHeading.ColumnDefinitions.Add(new ColumnDefinition()); updateHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var updateTitle = Caption("可用更新", "Talos.Label"); updateTitle.VerticalAlignment = VerticalAlignment.Center;
        updateHeading.Children.Add(updateTitle); SetButton(check, "Talos.PrimaryButton"); Grid.SetColumn(check, 1); updateHeading.Children.Add(check);
        update.Children.Add(updateHeading);
        available.Margin = new Thickness(0, 12, 0, 10); available.SetResourceReference(TextBlock.ForegroundProperty, "Talos.Gold"); update.Children.Add(available);
        notes.Text = "只有点击检查或下载更新时才会联网。没有网络时，可以继续使用当前版本，也可以导入离线更新。";
        notes.SetResourceReference(TextBox.BackgroundProperty, "Talos.Raised");
        var notesFrame = new Border { CornerRadius = new CornerRadius(9), ClipToBounds = true, Child = notes };
        notesFrame.SetResourceReference(Border.BackgroundProperty, "Talos.Raised"); update.Children.Add(notesFrame);
        var actions = new WrapPanel { Margin = new Thickness(-3, 12, -3, 0) };
        SetButton(install, "Talos.PrimaryButton"); SetButton(local, "Talos.SecondaryButton");
        actions.Children.Add(install); actions.Children.Add(local); update.Children.Add(actions);
        cards.Children.Add(Card(update));
        var body = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
        Grid.SetRow(body, 1); root.Children.Add(body);

        var footerContent = new StackPanel(); footerContent.Children.Add(progress);
        var footerLine = new Grid(); footerLine.ColumnDefinitions.Add(new ColumnDefinition()); footerLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.SetResourceReference(TextBlock.ForegroundProperty, "Talos.Muted");
        var statusScroll = new ScrollViewer { Content = status, MaxHeight = 76, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        footerLine.Children.Add(statusScroll);
        SetButton(cancel, "Talos.SecondaryButton"); cancel.MinWidth = 158; cancel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(cancel, 1); footerLine.Children.Add(cancel); footerContent.Children.Add(footerLine);
        var footer = Card(footerContent); footer.Margin = new Thickness(0); footer.Padding = new Thickness(15, 13, 12, 13);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        Content = root;
        check.Click += async (_, _) => await RunAsync(async token =>
        {
            var localState = await service.Value.StatusAsync(token).ConfigureAwait(false);
            var candidate = await service.Value.CheckAsync(token).ConfigureAwait(false);
            return () => { manifest = candidate; notes.Text = string.Join("\n\n", candidate.Notes.Select(note => "• " + note)); Applied(localState, CandidateMessage(candidate, localState)); };
        });
        install.Click += async (_, _) =>
        {
            if (manifest is null || !CanDownload(manifest, current)) return;
            var candidate = manifest;
            await RunAsync(async token =>
            {
                var localState = await service.Value.StatusAsync(token).ConfigureAwait(false);
                if (!CanDownload(candidate, localState)) return () => Applied(localState, CandidateMessage(candidate, localState));
                var update = await service.Value.InstallAsync(candidate, NewProgress(), token).ConfigureAwait(false);
                return () => Applied(update, "新版本已安装。点击重启后使用；当前窗口继续保持本版。");
            }, applyCommittedResult: true);
        };
        rollback.Click += async (_, _) => await RunAsync(async token => { var update = await service.Value.RollbackAsync(null, token).ConfigureAwait(false); return () => Applied(update, "已切换到健康旧版，点击重启使用。"); }, applyCommittedResult: true);
        local.Click += async (_, _) => await ImportLocalAsync(); cancel.Click += (_, _) => CancelOrReturn();
        restart.Click += (_, _) => Restart();
        Loaded += async (_, _) => await RunAsync(async token => { var update = await service.Value.StatusAsync(token).ConfigureAwait(false); return () => Applied(update, update.ActiveValid ? "本地文件已验证，未连接更新网站。" : "当前版本文件异常，可回退健康旧版或导入更新恢复。"); });
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; CancelOrReturn(); } };
        Closed += (_, _) => { closed = true; epoch++; RequestCancellation(updateUi: false); if (!busy) DisposeService(); };
        Controls();
    }

    private static TextBlock Caption(string text, string resource)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(StyleProperty, resource); return label;
    }
    private static Border Card(UIElement content)
    {
        var card = new Border { Child = content, Padding = new Thickness(18), CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12) };
        card.SetResourceReference(Border.BackgroundProperty, "Talos.Surface"); card.SetResourceReference(Border.BorderBrushProperty, "Talos.Border"); return card;
    }
    private static void SetButton(Button button, string resource)
    {
        button.SetResourceReference(StyleProperty, resource); button.Margin = new Thickness(3); button.Padding = new Thickness(14, 8, 14, 8); button.MinHeight = 37;
    }

    private sealed class CallbackProgress(Action<UpdateProgress> callback) : IProgress<UpdateProgress> { public void Report(UpdateProgress value) => callback(value); }
    private IProgress<UpdateProgress> NewProgress()
    {
        long operation = epoch;
        return new CallbackProgress(value =>
        {
            if (closed || Dispatcher.HasShutdownStarted) return;
            try { Dispatcher.BeginInvoke(new Action(() => { if (closed || operation != epoch || !busy || work?.IsCancellationRequested != false) return; progress.IsIndeterminate = value.Total <= 0; if (value.Total > 0) { progress.Maximum = value.Total; progress.Value = value.Done; } status.Text = value.Message; })); }
            catch (InvalidOperationException) { }
        });
    }
    private void Controls()
    {
        if (closed) return;
        check.IsEnabled = !busy; local.IsEnabled = !busy; rollback.IsEnabled = !busy;
        restart.IsEnabled = !busy && current?.ActiveValid == true;
        install.IsEnabled = !busy && manifest is not null && CanDownload(manifest, current);
        if (manifest is not null && current is not null)
        {
            var comparison = CompareCandidate(manifest, current);
            available.Text = comparison switch
            {
                < 0 => $"当前已是较新版本 · 线上版本 {manifest.Version}",
                0 when current.ActiveValid => $"当前已是最新版本 · {manifest.Version}",
                0 => $"当前版本可修复 · {manifest.Version}",
                > 0 => $"可用版本：{manifest.Version} · {manifest.Package.Bytes / 1048576d:F2} MB",
                _ => "本地版本信息尚不能确认，请使用离线更新恢复。"
            };
            install.Content = comparison == 0 && !current.ActiveValid ? "重新下载并修复" : "下载并安装";
        }
        cancel.Content = busy ? work?.IsCancellationRequested == true ? "正在取消…" : "取消当前操作" : "取消 / 返回助手";
        cancel.IsEnabled = !busy || work?.IsCancellationRequested != true;
    }
    private int? CompareCandidate(ReleaseManifest candidate, UpdateStatus localState)
    {
        // A missing active pointer must not let an older online feed replace the
        // version currently running from an offline package.
        var baseline = localState.ActiveVersion ?? context.Version;
        return System.Version.TryParse(candidate.Version, out var remote) && System.Version.TryParse(baseline, out var active)
            ? remote.CompareTo(active) : null;
    }
    private bool CanDownload(ReleaseManifest candidate, UpdateStatus? localState)
    {
        if (localState is null) return false;
        var comparison = CompareCandidate(candidate, localState);
        return comparison > 0 || comparison == 0 && !localState.ActiveValid;
    }
    private string CandidateMessage(ReleaseManifest candidate, UpdateStatus localState) => CompareCandidate(candidate, localState) switch
    {
        < 0 => "当前已启用较新版本，无需下载线上旧版。需要恢复旧版时，请使用“回退旧版”。",
        0 when localState.ActiveValid => "当前已是最新版本，无需重复安装。",
        0 => "当前版本的本地文件需要修复，可以重新下载同一版本。",
        > 0 => "更新说明已验证；点击下载按钮后才下载。",
        _ => "无法确认本地版本号，已停用下载。请导入离线更新或回退已验证旧版。"
    };
    private void Applied(UpdateStatus update, string message) { if (closed) return; current = update; version.Text = $"当前正在运行：{context.Version}\n已启用：{update.ActiveVersion ?? "尚未安装"}{(update.ActiveValid ? "" : "（文件尚未验证或异常）")}"; status.Text = message + (update.Warnings.Count > 0 ? "\n" + string.Join("\n", update.Warnings) : ""); Controls(); }
    private void CancelOrReturn()
    {
        if (closed) return;
        if (!busy) { Close(); return; }
        RequestCancellation(updateUi: true);
    }
    private void RequestCancellation(bool updateUi)
    {
        if (work is null || work.IsCancellationRequested) return;
        cancellationCallbacks = work.CancelAsync();
        if (updateUi && !closed) { status.Text = "正在取消当前操作；安全收尾完成后可关闭返回。提交前取消会保留原启用版本。"; progress.IsIndeterminate = true; Controls(); }
    }
    private void DisposeService()
    {
        if (serviceDisposed || !service.IsValueCreated) return;
        serviceDisposed = true; service.Value.Dispose();
    }
    private async Task RunAsync(Func<CancellationToken, Task<Action>> operation, bool applyCommittedResult = false)
    {
        if (busy || closed) return;
        busy = true; long operationEpoch = ++epoch; var operationWork = new CancellationTokenSource(); work = operationWork; var token = operationWork.Token; cancellationCallbacks = Task.CompletedTask;
        progress.IsIndeterminate = true; status.Text = "正在处理…"; Controls();
        try
        {
            var apply = await Task.Run(() => operation(token));
            if (closed || operationEpoch != epoch) return;
            if (!applyCommittedResult) token.ThrowIfCancellationRequested();
            apply();
        }
        catch (OperationCanceledException) { if (!closed && operationEpoch == epoch) status.Text = "本次操作已取消。可以继续使用当前助手，或关闭此窗口返回。"; }
        catch (Exception error) { if (!closed && operationEpoch == epoch) status.Text = "操作未完成：" + error.Message + "\n可继续当前助手，或回退 / 离线导入恢复。"; }
        finally
        {
            try { await cancellationCallbacks; } catch (Exception) { /* Cancellation callbacks must not interrupt disposal. */ }
            if (ReferenceEquals(work, operationWork)) { work = null; busy = false; }
            operationWork.Dispose();
            if (closed) DisposeService();
            else if (operationEpoch == epoch) { progress.IsIndeterminate = false; Controls(); }
        }
    }
    private async Task ImportLocalAsync()
    {
        var feed = new OpenFileDialog { Title = "选择签名更新JSON", Filter = "签名更新文件|*.json" }; if (feed.ShowDialog(this) != true) return;
        var zip = new OpenFileDialog { Title = "选择与签名配套的ZIP", Filter = "更新ZIP|*.zip" }; if (zip.ShowDialog(this) != true) return;
        string feedPath = feed.FileName, packagePath = zip.FileName;
        await RunAsync(async token => { var signed = await ReadBoundedAsync(feedPath, UpdateService.MaximumFeedBytes, token).ConfigureAwait(false); var package = await ReadBoundedAsync(packagePath, UpdateService.MaximumPackageBytes, token).ConfigureAwait(false); var update = await service.Value.InstallBootstrapAsync(signed, package, token).ConfigureAwait(false); return () => Applied(update, "离线包已验证处理，已启用版本如上。点击重启使用。"); }, applyCommittedResult: true);
    }
    private static async Task<byte[]> ReadBoundedAsync(string file, long limit, CancellationToken token)
    {
        if (new FileInfo(file).Length > limit) throw new InvalidOperationException($"所选文件过大，允许上限为 {limit / 1048576d:F0} MB。");
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        using var result = new MemoryStream(); var buffer = new byte[65536]; int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (result.Length + count > limit) throw new InvalidOperationException("文件读取期间超过大小上限，已停止导入。");
            await result.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return result.ToArray();
    }
    private void Restart()
    {
        try
        {
            if (!File.Exists(context.HostPath)) throw new InvalidOperationException("固定启动器不存在，请重新解压完整包。");
            Process.Start(new ProcessStartInfo(context.HostPath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(context.HostPath) ?? context.VersionRoot });
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception error) { status.Text = "重启未完成：" + error.Message; }
    }
}

using System;
using System.Text.Json;
using System.Threading;
using Talos.Updates;

namespace Talos.App;

public static class AppHost
{
    public static int Run(string contextJson)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("请通过正式启动器在 STA 线程启动助手。");
        var context = JsonSerializer.Deserialize<LaunchContext>(contextJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("启动上下文无效，请重新打开正式启动器。");
        var application = System.Windows.Application.Current ?? new System.Windows.Application();
        Theme.Initialize(application);
        application.ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
        return application.Run(new MainWindow(context));
    }
}

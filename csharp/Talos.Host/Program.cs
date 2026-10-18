using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Talos.Storage;
using Talos.Updates;

namespace Talos.Host;

internal static class Program
{
    private const string FeedUrl = "https://raw.githubusercontent.com/Sakimie/torchlightinfinite/talos-updates/csharp/latest.json";

    [STAThread]
    public static int Main()
    {
        string appDirectory = AppContext.BaseDirectory;
        string dataRoot = Path.Combine(appDirectory, "data");
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("此程序用于 Windows 10/11 x64。");
            string hostPath = Environment.ProcessPath ?? throw new IOException("无法定位启动程序。");
            appDirectory = Path.GetDirectoryName(Path.GetFullPath(hostPath)) ?? throw new IOException("无法定位程序目录。");
            dataRoot = Path.Combine(appDirectory, "data");
            string legacyRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TalosHelperCSharp", "preview");
            dataRoot = PortableStorage.Prepare(appDirectory, legacyRoot).DataRoot;
            string key = Encoding.UTF8.GetString(Resource("Talos.PublicKey"));
            using var updates = new UpdateService(dataRoot, key, FeedUrl);
            var status = updates.InstallBootstrapAsync(Resource("Talos.BootstrapFeed"), Resource("Talos.BootstrapPackage")).GetAwaiter().GetResult();
            if (!status.ActiveValid && status.InstalledVersions.Count > 0)
            {
                var choice = System.Windows.MessageBox.Show("当前版本文件未能通过校验。是否尝试回退到本机保留的健康版本？", "TalosHelper 启动恢复", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
                if (choice == System.Windows.MessageBoxResult.Yes) status = updates.RollbackAsync().GetAwaiter().GetResult();
            }
            string versionRoot = updates.GetActiveDirectoryAsync().GetAwaiter().GetResult();
            var context = new LaunchContext(status.ActiveVersion ?? throw new IOException("未找到已安装版本。"), versionRoot, dataRoot, hostPath, key, FeedUrl);
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                if (name.Name != "Talos.Core" && name.Name != "Talos.App") return null;
                string file = Path.Combine(versionRoot, name.Name + ".dll");
                return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
            };
            Assembly app = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(versionRoot, "Talos.App.dll"));
            MethodInfo entry = app.GetType("Talos.App.AppHost", throwOnError: true)!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException("C# 界面组件缺少启动入口。");
            object? result = entry.Invoke(null, new object[] { JsonSerializer.Serialize(context, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
            return result is int code ? code : 0;
        }
        catch (Exception exception)
        {
            Exception error = exception is TargetInvocationException { InnerException: not null } wrapper ? wrapper.InnerException! : exception;
            try
            {
                File.WriteAllText(Path.Combine(appDirectory, "last-startup-error.txt"), error.ToString(), Encoding.UTF8);
            }
            catch { }
            System.Windows.MessageBox.Show("便携版未能启动：\n" + error.Message + "\n\n数据目录：" + dataRoot + "\n请将完整程序放在有写入权限的文件夹；本版不会改用 AppData。错误详情已尝试保存到程序旁的 last-startup-error.txt。", "TalosHelper 启动诊断", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return 1;
        }
    }

    private static byte[] Resource(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidDataException("完整包缺少内嵌资源：" + name);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

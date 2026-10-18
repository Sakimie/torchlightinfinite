using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Talos.App;

/// <summary>Shared application resources, including owned update/review/overlay windows.</summary>
public static class Theme
{
    private static readonly Uri DictionaryUri = new("/Talos.App;component/Themes/Dark.xaml", UriKind.Relative);

    public static void Initialize(Application application)
    {
        foreach (var dictionary in application.Resources.MergedDictionaries)
            if (dictionary.Source == DictionaryUri) return;
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DictionaryUri });
    }

    public static void Apply(Window window)
    {
        if (Application.Current is { } application) Initialize(application);
        else window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DictionaryUri });
        window.SetResourceReference(ControlBackground, "Talos.Background");
        window.SetResourceReference(ControlForeground, "Talos.Text");
        window.FontFamily = new FontFamily("Microsoft YaHei UI");
        window.FontSize = 13;
        window.UseLayoutRounding = true;
        window.SnapsToDevicePixels = true;
        window.SourceInitialized -= ApplyTitleBar;
        window.SourceInitialized += ApplyTitleBar;
        ApplyTitleBar(window, EventArgs.Empty);
    }

    private static void ApplyTitleBar(object? sender, EventArgs args)
    {
        if (!OperatingSystem.IsWindows() || sender is not Window window) return;
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            int enabled = 1;
            _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException) { } // Older Windows keeps the standard system title bar.
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    private static readonly DependencyProperty ControlBackground = System.Windows.Controls.Control.BackgroundProperty;
    private static readonly DependencyProperty ControlForeground = System.Windows.Controls.Control.ForegroundProperty;
}

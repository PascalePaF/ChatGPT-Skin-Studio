using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace ChatGptSkinStudio;

internal static class UiTheme
{
    internal static void Apply(FrameworkElement window, string name)
    {
        string[] colors = name switch
        {
            "light" => new[] { "#F4F6F8", "#FFFFFF", "#E9EEF2", "#FFFFFF", "#15232D", "#526675", "#D6DFE6", "#D9EEE8", "#E9F1F2", "#186B58", "#FFFFFF", "#D9E5E9", "#E8EDF1" },
            "halloween" => new[] { "#16111D", "#201829", "#2A2034", "#261C30", "#F9F0E5", "#B7A7C1", "#49324E", "#493022", "#36273E", "#FFB56B", "#301B12", "#483652", "#100D16" },
            _ => new[] { "#10171E", "#172129", "#202E38", "#1A2731", "#EDF4F5", "#9CAFB9", "#30424D", "#23433E", "#23343F", "#7DE2CA", "#102D25", "#2C414D", "#0B1117" }
        };
        string[] keys = { "Canvas", "Sidebar", "Control", "Card", "Ink", "Muted", "Line", "Selected", "Hover", "Accent", "AccentInk", "Thumb", "Well" };
        for (int i = 0; i < keys.Length; i++) window.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        if (window is Window owner && new WindowInteropHelper(owner).Handle is var handle && handle != nint.Zero)
        {
            int dark = name == "light" ? 0 : 1;
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0) DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
            var caption = (Color)ColorConverter.ConvertFromString(colors[0]);
            var text = (Color)ColorConverter.ConvertFromString(colors[4]);
            int captionColor = caption.R | caption.G << 8 | caption.B << 16;
            int textColor = text.R | text.G << 8 | text.B << 16;
            DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(int));
            DwmSetWindowAttribute(handle, 36, ref textColor, sizeof(int));
        }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int bytes);
}

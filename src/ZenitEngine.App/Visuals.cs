using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

static class Palette
{
    public static readonly Color FlareColor = Color.Parse("#8EC5FF");
    public static readonly IBrush Panel = Brush.Parse("#B80D1117");
    public static readonly IBrush Line = Brush.Parse("#1E2531");
    public static readonly IBrush Ink = Brush.Parse("#EEF1F6");
    public static readonly IBrush Muted = Brush.Parse("#8A93A6");
    public static readonly IBrush Flare = new SolidColorBrush(FlareColor);
    public static readonly IBrush Hot = Brush.Parse("#FF8A5C");
    public static readonly IBrush Warn = Brush.Parse("#F5C26B");
    public static readonly IBrush Ok = Brush.Parse("#7AD7A8");
    public static readonly IBrush Track = Brush.Parse("#1A202A");

    public static IBrush For(Severity s) => s switch
    {
        Severity.Critical => Hot, Severity.Warning => Warn, Severity.Info => Flare, _ => Ok,
    };

    public static IBrush ForTemp(double c) => c >= 95 ? Hot : c >= 85 ? Warn : Ink;
}

static class Fonts
{
    public static readonly FontFamily Body = new("avares://ZenitEngine.App/Assets/Fonts#Archivo");
    // Expanded faces are referenced by file: folder-wide lookups resolve every Archivo name to the normal width.
    public static readonly FontFamily Display = new("avares://ZenitEngine.App/Assets/Fonts/ArchivoExpanded-SemiBold.ttf#Archivo Expanded SemiBold");
}

namespace GuiSsh.Client.Models;

public enum WindowType
{
    FileManager,
    Editor,
    ImageViewer
}

public class DesktopWindowState
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = string.Empty;
    public WindowType Type { get; set; }
    public double X { get; set; } = 50;
    public double Y { get; set; } = 50;
    public double Width { get; set; } = 700;
    public double Height { get; set; } = 500;
    public int ZIndex { get; set; } = 1;
    public bool IsMinimized { get; set; }
    public bool IsMaximized { get; set; }
    public Dictionary<string, object?> Data { get; set; } = new();
}

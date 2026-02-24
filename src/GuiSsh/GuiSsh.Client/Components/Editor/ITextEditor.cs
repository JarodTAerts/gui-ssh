namespace GuiSsh.Client.Components.Editor;

public interface ITextEditor
{
    string Content { get; set; }
    string FileName { get; set; }
    bool IsReadOnly { get; set; }
    bool IsDirty { get; }
    event EventHandler<string>? OnSave;
}

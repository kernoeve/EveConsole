using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class AgentDocumentView : ReactiveUserControl<AgentDocumentViewModel>
{
    public AgentDocumentView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind();
    }

    private AgentDocumentViewModel? _bound;

    private void Bind()
    {
        if (DataContext is not AgentDocumentViewModel vm || ReferenceEquals(vm, _bound)) return;
        _bound = vm;

        // Rendered once, here, rather than through a value converter: the result is a tree of
        // controls, the document does not change after the agent wrote it, and re-rendering it on
        // every layout pass would be work with no reader.
        Body.Content = MarkdownRenderer.Render(vm.Markdown);

        vm.CopyToClipboard = async text =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(text);
        };

        vm.SaveTextFile = SaveTextFileAsync;
    }

    private async Task<string?> SaveTextFileAsync(string suggestedName, string contents)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return null;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title               = "Save document",
            SuggestedFileName   = suggestedName,
            DefaultExtension    = "md",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("Markdown")  { Patterns = ["*.md"]  },
                new FilePickerFileType("Text")      { Patterns = ["*.txt"] },
                new FilePickerFileType("All files") { Patterns = ["*"]     },
            ],
        });
        if (file is null) return null;

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(contents);
        return file.Name;
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AgentDocumentViewModel vm) await vm.SaveMarkdownAsync();
    }
}

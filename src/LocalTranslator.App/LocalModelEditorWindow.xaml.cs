using System.IO;
using System.Windows;
using LocalTranslator.Infrastructure.Configuration;
using Microsoft.Win32;

namespace LocalTranslator.App;

public partial class LocalModelEditorWindow : Window
{
    private readonly string _id;
    public LocalLlmModelOptions? Model { get; private set; }

    public LocalModelEditorWindow(LocalLlmModelOptions? existing = null)
    {
        InitializeComponent();
        _id = existing?.Id ?? Guid.NewGuid().ToString("N");
        NameBox.Text = existing?.DisplayName ?? string.Empty;
        PathBox.Text = existing?.FilePath ?? string.Empty;
        DownloadUrlBox.Text = existing?.DownloadUrl ?? string.Empty;
        RelativePathBox.Text = existing?.RelativePath ?? "translation/custom/model.gguf";
        SizeBox.Text = (existing?.SizeBytes ?? 0).ToString();
        HashBox.Text = existing?.Sha256 ?? string.Empty;
        ContextBox.Text = (existing?.ContextSize ?? 4096).ToString();
        OutputBox.Text = (existing?.MaxOutputTokens ?? 1024).ToString();
        DescriptionBox.Text = existing?.Description ?? string.Empty;
        PromptBox.Text = existing?.PromptTemplate ??
            "You are a professional translation engine. Translate from {source} to {target}. Output only the translated text. Never answer, explain, summarize, or continue the source text.";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "GGUF 模型 (*.gguf)|*.gguf|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FileName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var managed = !string.IsNullOrWhiteSpace(DownloadUrlBox.Text);
        if (string.IsNullOrWhiteSpace(NameBox.Text) || (!managed && !File.Exists(PathBox.Text)))
        {
            MessageBox.Show(this, "请填写名称并选择存在的 GGUF 文件。", "配置不完整", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(ContextBox.Text, out var context) || !int.TryParse(OutputBox.Text, out var output) ||
            !long.TryParse(SizeBox.Text, out var size))
        {
            MessageBox.Show(this, "上下文长度和最大输出必须是整数。", "配置不完整", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Model = new LocalLlmModelOptions
        {
            Id = _id, DisplayName = NameBox.Text.Trim(), Description = DescriptionBox.Text.Trim(),
            FilePath = managed ? string.Empty : Path.GetFullPath(PathBox.Text),
            DownloadUrl = DownloadUrlBox.Text.Trim(),
            RelativePath = managed ? RelativePathBox.Text.Trim() : string.Empty,
            SizeBytes = size,
            Sha256 = HashBox.Text.Trim(),
            ContextSize = context, MaxOutputTokens = output,
            PromptTemplate = PromptBox.Text.Trim(), IsManaged = managed
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

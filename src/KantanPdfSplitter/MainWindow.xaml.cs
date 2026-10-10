using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using KantanPdfSplitter.Core;
using Microsoft.Win32;

namespace KantanPdfSplitter;

public partial class MainWindow : Window
{
    private readonly PdfSplitService _service = new();
    private PdfInfo? _info;
    private IReadOnlyList<PageRange>? _plan;
    private bool _busy;
    private string? _completedFolder;
    public MainWindow() => InitializeComponent();

    private async void ChoosePdf_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog { Filter = "PDFファイル (*.pdf)|*.pdf", Multiselect = false, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await LoadPdfAsync(dialog.FileName);
    }
    private void Pdf_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_busy && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files &&
            Path.GetExtension(files[0]).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void Pdf_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_busy) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files && Path.GetExtension(files[0]).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            await LoadPdfAsync(files[0]);
        else SetStatus("PDFを1つだけ選んでください。", true);
    }
    internal async Task LoadPdfAsync(string path)
    {
        if (_busy) return;
        _info = null; _plan = null; PreviewList.ItemsSource = null; FileSummary.Text = "PDFを確認しています…";
        SetBusy(true); ClearCompletion(); SetStatus("PDFを確認しています…");
        try
        {
            _info = await _service.InspectAsync(path);
            FileSummary.Text = $"{Path.GetFileName(path)}  ・  {_info.PageCount}ページ";
            FileSummary.ToolTip = path;
            if (string.IsNullOrEmpty(DestinationBox.Text)) DestinationBox.Text = Path.GetDirectoryName(path);
            UpdatePlan();
        }
        catch (Exception error) { FileSummary.Text = "PDFが選択されていません"; ShowError(error); }
        finally { SetBusy(false); }
    }
    private void PlanChanged(object sender, RoutedEventArgs e) { if (IsInitialized) { ClearCompletion(); UpdatePlan(); } }
    private void UpdatePlan()
    {
        CutsPanel.Visibility = AtPages.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _plan = null;
        PreviewList.ItemsSource = null;
        if (_info != null)
        {
            try
            {
                _plan = SplitPlan.Create(_info, EveryPage.IsChecked == true, CutsBox.Text);
                PreviewList.ItemsSource = _plan;
                PreviewTitle.Text = $"分割結果のプレビュー  ・  {_plan.Count}ファイル";
                SetStatus("元のPDFは変更されません。");
            }
            catch (PdfSplitException error) { PreviewTitle.Text = "分割結果のプレビュー"; SetStatus(error.Message, true); }
        }
        UpdateRunState();
    }
    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFolderDialog { Title = "分割したPDFの保存先を選ぶ", Multiselect = false };
        if (Directory.Exists(DestinationBox.Text)) dialog.InitialDirectory = DestinationBox.Text;
        if (dialog.ShowDialog(this) == true) { DestinationBox.Text = dialog.FolderName; ClearCompletion(); UpdatePlan(); }
    }
    private async void Split_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _info == null || _plan == null || string.IsNullOrWhiteSpace(DestinationBox.Text)) return;
        var folder = DestinationBox.Text;
        SetBusy(true); ClearCompletion(); SetStatus("PDFを分割しています…");
        Progress.IsIndeterminate = false; Progress.Maximum = _plan.Count; Progress.Value = 0;
        try
        {
            var paths = await _service.SplitAsync(_info, _plan, folder, new Progress<SplitProgress>(p =>
            {
                Progress.Value = p.Completed;
                SetStatus($"PDFを分割しています… {p.Completed} / {p.Total}");
            }));
            _completedFolder = folder; OpenFolderButton.Visibility = Visibility.Visible;
            SetStatus($"完了しました。{paths.Count}ファイルを保存しました。");
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }
    private void SetBusy(bool value)
    {
        _busy = value; InputPanel.IsEnabled = !value;
        Progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed; Progress.IsIndeterminate = value;
        SplitButton.Content = value ? "処理中…" : "PDFを分割する";
        UpdateRunState();
    }
    private void UpdateRunState() => SplitButton.IsEnabled = !_busy && _info != null && _plan?.Count > 0 && !string.IsNullOrWhiteSpace(DestinationBox.Text);
    private void ClearCompletion() { _completedFolder = null; OpenFolderButton.Visibility = Visibility.Collapsed; }
    private void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(error ? System.Windows.Media.Color.FromRgb(164, 37, 37) : System.Windows.Media.Color.FromRgb(23, 50, 77));
    }
    private void ShowError(Exception error) => SetStatus(error switch
    {
        PdfSplitException => error.Message,
        OutOfMemoryException => "このPDFを処理するためのメモリが足りません。他のアプリを終了してお試しください。",
        _ => "処理を完了できませんでした。PDFと保存先を確認してお試しください。"
    }, true);
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { if (_completedFolder != null) Process.Start(new ProcessStartInfo(_completedFolder) { UseShellExecute = true }); }
        catch (Exception) { SetStatus("保存先を開けませんでした。エクスプローラーから開いてください。", true); }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true; SetStatus("処理が完了してから閉じてください。");
    }
}

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using Microsoft.Win32;

namespace RevitAiRender;

public partial class RenderPanel : UserControl, IDockablePaneProvider
{
    private readonly RevitViewService views;
    private readonly CancellationTokenSource shutdown = new();
    private ViewList? viewList;
    private RenderJobService? jobService;
    private bool refreshPending;
    private bool revitBusy;
    private bool updatingSelection;
    private bool jobBusy;
    private string? activeJobId;
    private volatile Tuple<string, JobProgress>? activeProgress;
    private bool resultLoading;
    private int resultRevision;
    private RenderJob? resultJob;
    private BitmapSource? resultSource;
    private BitmapSource? resultImage;
    private RenderPreviewWindow? largePreview;
    internal IntPtr HostWindow { private get; set; }
    private string? previewPath;
    private ViewChoice? previewView;
    private bool IsMasked => ModeBox?.SelectedIndex == 1;

    internal RenderPanel(RevitViewService views)
    {
        this.views = views;
        InitializeComponent();
        MaskCanvas.Changed += (_, _) => UpdateControls();
        UpdateControls();
        IsVisibleChanged += (_, args) =>
        {
            if ((bool)args.NewValue)
            {
                ScheduleRefresh();
                Dispatcher.BeginInvoke(new Action(() => _ = RefreshHistoryAsync()));
            }
        };
    }

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = this;
        data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
    }

    internal void OpenSection(int section)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (section == 3)
                Tabs.SelectedItem = ResultTab;
            else if (section == 2)
            {
                Tabs.SelectedItem = HistoryTab;
                _ = RefreshHistoryAsync();
            }
            else
            {
                Tabs.SelectedItem = NewTab;
                ModeBox.SelectedIndex = section;
            }
            ScheduleRefresh();
        }));
    }

    internal void StopMonitoring()
    {
        shutdown.Cancel();
        largePreview?.Close();
    }

    internal void ScheduleRefresh()
    {
        if (refreshPending || shutdown.IsCancellationRequested)
            return;
        refreshPending = true;
        QueueRefresh();
    }

    private void QueueRefresh() => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
    {
        if (!IsVisible)
            refreshPending = false;
        else if (!revitBusy)
            _ = SyncViewsAsync();
    }));

    private async Task SyncViewsAsync(bool selectActive = false, bool forcePreview = false)
    {
        if (revitBusy)
            return;
        refreshPending = false;
        var prior = Views.SelectedItem as ViewChoice;
        try
        {
            SetRevitBusy(true);
            viewList = await views.ListAsync();
            ProjectText.Text = viewList.Project.Length == 0 ? "Откройте проект Revit" : viewList.Project;
            var active = viewList.Views.FirstOrDefault(view => view.UniqueId == viewList.ActiveId);
            var previous = viewList.Views.FirstOrDefault(view =>
                view.Document == prior?.Document && view.UniqueId == prior.UniqueId);
            var selected = (selectActive ? active : previous) ?? previous ?? active ?? viewList.Views.FirstOrDefault();
            updatingSelection = true;
            Views.ItemsSource = viewList.Views;
            Views.SelectedItem = selected;
            updatingSelection = false;

            if (selected == null)
            {
                ShowPreview(null);
                SetPreviewStatus(viewList.Project.Length == 0 ? "" : "В проекте нет доступных 3D-видов.");
            }
            else if (forcePreview || selected.UniqueId != prior?.UniqueId || selected.Document != prior.Document)
            {
                await ExportPreviewAsync(selected);
            }
        }
        catch (Exception error) { ShowPreviewError(error); }
        finally { SetRevitBusy(false); }
    }

    private async void ViewChanged(object sender, SelectionChangedEventArgs args)
    {
        if (updatingSelection || revitBusy || Views.SelectedItem is not ViewChoice view)
            return;
        try
        {
            SetRevitBusy(true);
            await ExportPreviewAsync(view);
        }
        catch (Exception error) { ShowPreviewError(error); }
        finally { SetRevitBusy(false); }
    }

    private async void RefreshPreview(object sender, RoutedEventArgs args) => await SyncViewsAsync(forcePreview: true);
    private async void UseActive(object sender, RoutedEventArgs args) => await SyncViewsAsync(selectActive: true, forcePreview: true);
    private void PromptChanged(object sender, TextChangedEventArgs args) => UpdateControls();

    private void ModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (MaskCanvas == null || MaskTools == null || PreviewFrame == null || MaskHint == null)
            return;
        UpdateControls();
    }

    private void MaskToolChanged(object sender, SelectionChangedEventArgs args)
    {
        if (MaskCanvas != null && MaskToolBox.SelectedIndex >= 0)
            MaskCanvas.Tool = (MaskTool)MaskToolBox.SelectedIndex;
        UpdateControls();
    }

    private void MaskSizeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (MaskCanvas != null && MaskSizeBox.SelectedIndex >= 0)
            MaskCanvas.BrushDiameter = new[] { 16, 40, 88 }[MaskSizeBox.SelectedIndex];
    }

    private void ZoomChanged(object sender, SelectionChangedEventArgs args)
    {
        if (MaskCanvas != null && ZoomBox.SelectedIndex >= 0)
            MaskCanvas.SetZoom(new[] { 1.0, 1.5, 2.0, 3.0 }[ZoomBox.SelectedIndex]);
    }

    private void ClearMask(object sender, RoutedEventArgs args) => MaskCanvas.ClearMask();

    private async Task ExportPreviewAsync(ViewChoice view)
    {
        ShowPreview(null);
        SetPreviewStatus("Экспорт кадра Revit…");
        var path = await views.ExportAsync(view, LocalFiles.NewPreview(), 768);
        var frame = await Task.Run(() => LocalFiles.OpenPng(path));
        previewPath = path;
        previewView = view;
        ShowPreview(frame);
        SetPreviewStatus("");
    }

    private void ShowPreview(BitmapSource? source)
    {
        if (source == null)
        {
            previewPath = null;
            previewView = null;
        }
        PreviewImage.Source = source;
        MaskCanvas.SetFrame(source);
        PreviewEmpty.Visibility = source == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetPreviewStatus(string message)
    {
        PreviewStatus.Text = message;
        PreviewStatus.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPreviewError(Exception error) =>
        SetPreviewStatus(Describe(error, "panel-revit", "Не удалось получить кадр или список видов."));

    private async void Generate(object sender, RoutedEventArgs args)
    {
        if (jobBusy || revitBusy || Views.SelectedItem is not ViewChoice view || string.IsNullOrWhiteSpace(PromptBox.Text))
            return;
        var masked = IsMasked;
        var source = previewPath;
        if (masked && (source == null || previewView?.Document != view.Document ||
                       previewView?.UniqueId != view.UniqueId || !MaskCanvas.HasSelection))
            return;
        var mask = masked ? MaskCanvas.CaptureMask() : null;
        var prompt = PromptBox.Text;
        if (prompt.IndexOf('\0') >= 0)
        {
            UpdateProgress(new JobProgress("Удалите из промпта нулевой символ."));
            return;
        }

        await RunGenerationAsync(view, prompt, PreserveBox.IsChecked == true, mask, source);
    }

    internal async Task<RenderJob> StartMcpAsync(McpRequest request, CancellationToken token)
    {
        var id = McpPipeServer.McpId(request.Id);
        var prompt = request.Prompt;
        if (prompt == null || string.IsNullOrWhiteSpace(prompt) || prompt.Length > 8000 || prompt.IndexOf('\0') >= 0)
            throw new RenderException("prompt", "Укажите промпт длиной от 1 до 8000 символов.");
        if (string.IsNullOrWhiteSpace(request.Project) || string.IsNullOrWhiteSpace(request.ProjectToken))
            throw new RenderException("project-required", "Укажите название проекта из списка 3D-видов.");
        if (string.IsNullOrWhiteSpace(request.ViewId))
            throw new RenderException("view-required", "Укажите ID 3D-вида из списка Revit.");

        var previous = await Task.Run(() => ResultStorage.Find(id), token);
        if (previous != null)
        {
            if (previous.Legacy || previous.IsMasked || previous.ViewUniqueId != request.ViewId ||
                previous.Project != request.Project || previous.PreserveGeometry != request.PreserveGeometry || previous.Prompt != prompt)
                throw new RenderException("request-conflict", "Этот ID уже используется другой задачей.");
            return previous;
        }

        token.ThrowIfCancellationRequested();
        if (jobBusy || revitBusy)
            throw new RenderException("render-busy", "Дождитесь текущей операции AI Render.");
        SetRevitBusy(true);
        try
        {
            var list = await views.ListAsync();
            token.ThrowIfCancellationRequested();
            if (list.Project != request.Project)
                throw new RenderException("project-changed", "Активный проект сменился. Обновите список 3D-видов.");
            var view = list.Views.FirstOrDefault(candidate => candidate.UniqueId == request.ViewId);
            if (view == null)
                throw new RenderException("view-missing", "Указанный 3D-вид не найден в активном проекте.");
            if (McpPipeServer.ProjectToken(view) != request.ProjectToken)
                throw new RenderException("project-changed", "Активный проект сменился. Обновите список 3D-видов.");

            var accepted = new TaskCompletionSource<RenderJob>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = RunGenerationAsync(view, prompt!, request.PreserveGeometry, null, null, id, accepted);
            return await accepted.Task;
        }
        finally
        {
            if (!jobBusy)
                SetRevitBusy(false);
        }
    }

    internal JobProgress? ReadMcpProgress(string jobId)
    {
        var snapshot = activeProgress;
        return snapshot?.Item1 == jobId ? snapshot.Item2 : null;
    }

    private async Task RunGenerationAsync(ViewChoice view, string prompt, bool preserve, BitmapSource? mask,
        string? source, Guid? requestId = null, TaskCompletionSource<RenderJob>? accepted = null)
    {
        RenderJob? job = null;
        var masked = mask != null;
        try
        {
            jobBusy = true;
            SetRevitBusy(true);
            UpdateProgress(new JobProgress(masked ? "Сохранение кадра и маски…" : "Экспорт выбранного вида Revit…"));
            job = await Task.Run(() =>
            {
                var created = ResultStorage.Create(view, prompt, preserve, Settings.Load(), masked, requestId);
                ResultStorage.Save(created, replaceExisting: false);
                return created;
            });
            activeJobId = job.Id;
            activeProgress = Tuple.Create(job.Id, new JobProgress("Экспорт выбранного вида Revit…"));
            accepted?.TrySetResult(job);
            if (mask != null)
            {
                await Task.Run(() =>
                {
                    File.Copy(source!, job.SourcePath);
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(mask));
                    using var file = new FileStream(job.MaskPath, FileMode.CreateNew);
                    png.Save(file);
                });
            }
            else
            {
                await views.ExportAsync(view, job.DirectoryPath, 1440);
            }
            SetRevitBusy(false);
            await MonitorAsync(job, false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { accepted?.TrySetCanceled(); }
        catch (Exception error)
        {
            var message = Describe(error, "generate", "Не удалось запустить задачу.");
            accepted?.TrySetException(error as RenderException ?? new RenderException("start-failed", message));
            UpdateProgress(new JobProgress(message));
            if (job != null && !job.SubmissionAttempted)
            {
                try { await Task.Run(() => RenderJobService.Record(job, "failed", message)); }
                catch (Exception storageError) { Diagnostics.Exception("failed-job-save", storageError); }
                await RefreshHistoryAsync(job.Id);
            }
        }
        finally
        {
            activeJobId = null;
            activeProgress = null;
            jobBusy = false;
            SetRevitBusy(false);
        }
    }

    private async void Resume(object sender, RoutedEventArgs args)
    {
        if (jobBusy || HistoryBox.SelectedItem is not RenderJob job || !job.CanResume)
            return;
        try
        {
            jobBusy = true;
            UpdateControls();
            UpdateProgress(new JobProgress("Проверка задачи на Mac…"));
            await MonitorAsync(job, true);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            UpdateProgress(new JobProgress(Describe(error, "resume", "Не удалось проверить задачу.")));
        }
        finally
        {
            jobBusy = false;
            UpdateControls();
        }
    }

    private async Task MonitorAsync(RenderJob job, bool resume)
    {
        var service = new RenderJobService(new Progress<JobProgress>(UpdateProgress), shutdown.Token);
        jobService = service;
        UpdateControls();
        try
        {
            var finished = await Task.Run(() => service.RunAsync(job, resume));
            if (finished.State == "done")
            {
                UpdateProgress(new JobProgress(""));
                await ShowResultAsync(finished, openLarge: true);
            }
            else
            {
                UpdateProgress(new JobProgress(finished.Message ?? "Задача завершена."));
                if (finished.CanResume)
                    Tabs.SelectedItem = HistoryTab;
            }
            await RefreshHistoryAsync(finished.Id);
        }
        finally
        {
            jobService = null;
            UpdateControls();
        }
    }

    private void Cancel(object sender, RoutedEventArgs args)
    {
        jobService?.RequestCancellation();
        UpdateProgress(new JobProgress("Запрос отмены задачи…"));
    }

    private void UpdateProgress(JobProgress progress)
    {
        if (activeJobId != null)
            activeProgress = Tuple.Create(activeJobId, progress);
        JobStatus.Text = progress.Message;
        UpdateFooter();
        JobProgress.Visibility = progress.Fraction.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (progress.Fraction is double fraction)
            JobProgress.Value = Math.Max(0, Math.Min(1, fraction));
    }

    private async Task RefreshHistoryAsync(string? select = null)
    {
        try
        {
            var history = await Task.Run(ResultStorage.ReadHistory);
            var id = select ?? (HistoryBox.SelectedItem as RenderJob)?.Id;
            HistoryBox.ItemsSource = history;
            HistoryBox.SelectedItem = history.FirstOrDefault(item => item.Id == id) ?? history.FirstOrDefault();
        }
        catch (Exception error) { HistoryStatus.Text = Describe(error, "history", "Не удалось прочитать историю."); }
    }

    private void TabChanged(object sender, SelectionChangedEventArgs args)
    {
        if (args.Source != Tabs)
            return;
        UpdateFooter();
        if (Tabs.SelectedItem == ResultTab && resultJob == null && !resultLoading && ResultViewer != null)
            _ = ShowLatestResultAsync();
    }

    private void HistoryChanged(object sender, SelectionChangedEventArgs args)
    {
        if (HistoryStatus == null)
            return;
        var job = HistoryBox.SelectedItem as RenderJob;
        HistoryStatus.Text = job?.Message ?? (job == null ? "История пока пуста" : "Задача сохранена.");
        HistoryPrompt.Text = job?.Prompt ?? "";
        HistoryDetails.Visibility = job == null ? Visibility.Collapsed : Visibility.Visible;
        Parameters.Text = job == null ? "" : HistoryParameters(job);
        UpdateControls();
    }

    private static string HistoryParameters(RenderJob job)
    {
        var resolution = job.OutputWidth > 0 ? $" · {job.OutputWidth} × {job.OutputHeight}" : "";
        var seed = job.Seed.HasValue ? $" · seed {job.Seed.Value}" : "";
        var remote = job.RemoteId != null ? $" · {job.RemoteId}" : "";
        var geometry = job.PreserveGeometry ? "сохранять" : "свободная обработка";
        return $"{BackendName(job)}{resolution}{seed}{remote} · геометрия: {geometry}";
    }

    private static string BackendName(RenderJob job) => job.IsMasked ? "MFLUX" : "Siliconed";

    internal static string ResultDetails(RenderJob job, BitmapSource result) =>
        $"{BackendName(job)} · {result.PixelWidth} × {result.PixelHeight}";

    private async void OpenSelected(object sender, RoutedEventArgs args)
    {
        if (sender is ListBox && args.OriginalSource is DependencyObject from &&
            ItemsControl.ContainerFromElement(HistoryBox, from) == null)
            return;
        if (HistoryBox.SelectedItem is RenderJob job && job.State == "done")
            await ShowResultAsync(job);
    }

    private async Task ShowLatestResultAsync()
    {
        var revision = ++resultRevision;
        resultLoading = true;
        ResultViewer.Display(null, null, "Загружаем последний рендер…");
        try
        {
            var job = await Task.Run(() => ResultStorage.ReadHistory()
                .FirstOrDefault(item => item.State == "done" && File.Exists(item.ResultPath)));
            if (revision != resultRevision || shutdown.IsCancellationRequested)
                return;
            if (job != null && Tabs.SelectedItem == ResultTab)
                await ShowResultAsync(job);
            else
                ResultViewer.Display(null, null, "Рендер появится здесь после генерации");
        }
        catch (Exception error)
        {
            if (revision == resultRevision)
                ResultViewer.Display(null, null, Describe(error, "latest-result", "Не удалось открыть историю."));
        }
        finally
        {
            if (revision == resultRevision)
                resultLoading = false;
        }
    }

    internal async Task ShowResultAsync(RenderJob job, bool openLarge = false)
    {
        var revision = ++resultRevision;
        resultLoading = true;
        Tabs.SelectedItem = ResultTab;
        resultJob = null;
        resultSource = resultImage = null;
        ResultHeading.Visibility = ResultActions.Visibility = ResultFeedback.Visibility = Visibility.Collapsed;
        ResultViewer.Display(null, null, "Открываем рендер…");
        try
        {
            var pictures = await Task.Run(() =>
            {
                var result = LocalFiles.OpenPng(job.ResultPath);
                BitmapSource? source = null;
                try
                {
                    if (File.Exists(job.SourcePath))
                        source = LocalFiles.OpenPng(job.SourcePath);
                }
                catch (Exception error) { Diagnostics.Exception("result-source", error); }
                return (source, result);
            });
            if (revision != resultRevision || shutdown.IsCancellationRequested)
                return;

            resultJob = job;
            resultSource = pictures.source;
            resultImage = pictures.result;
            ResultTitle.Text = job.View;
            ResultProject.Text = $"{job.Project} · {ResultDetails(job, pictures.result)}";
            ResultHeading.Visibility = ResultActions.Visibility = Visibility.Visible;
            ResultViewer.Display(resultSource, resultImage, "");
            if (openLarge)
                ShowLargePreview();
        }
        catch (Exception error)
        {
            if (revision == resultRevision)
                ResultViewer.Display(null, null, Describe(error, "result-image", "Не удалось открыть PNG результата."));
        }
        finally
        {
            if (revision == resultRevision)
                resultLoading = false;
        }
    }

    private void OpenLarge(object sender, RoutedEventArgs args) => ShowLargePreview();

    private void ShowLargePreview()
    {
        if (resultJob == null || resultImage == null)
            return;
        try
        {
            if (largePreview == null)
            {
                var window = new RenderPreviewWindow();
                if (HostWindow != IntPtr.Zero)
                    new WindowInteropHelper(window).Owner = HostWindow;
                window.Closed += (_, _) => largePreview = null;
                window.Display(resultJob, resultSource, resultImage);
                largePreview = window;
                window.Show();
            }
            else
            {
                largePreview.Display(resultJob, resultSource, resultImage);
                if (largePreview.WindowState == WindowState.Minimized)
                    largePreview.WindowState = WindowState.Normal;
                largePreview.Activate();
            }
        }
        catch (Exception error)
        {
            largePreview = null;
            ResultFeedback.Text = Describe(error, "large-preview", "Не удалось открыть отдельное окно.");
            ResultFeedback.Visibility = Visibility.Visible;
        }
    }

    private async void SavePng(object sender, RoutedEventArgs args)
    {
        if (resultJob == null)
            return;
        var job = resultJob;
        var message = await SaveResultAsync(job, Window.GetWindow(this));
        if (message == null || resultJob != job)
            return;
        ResultFeedback.Text = message;
        ResultFeedback.Visibility = Visibility.Visible;
    }

    internal static async Task<string?> SaveResultAsync(RenderJob job, Window? owner)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить рендер",
            Filter = "Изображение PNG (*.png)|*.png",
            FileName = $"AI-Render-{job.Id.Substring(0, Math.Min(8, job.Id.Length))}.png",
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = false
        };
        try
        {
            if ((owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true)
                return null;
            await Task.Run(() => File.Copy(job.ResultPath, dialog.FileName, false));
            return "PNG сохранён.";
        }
        catch (IOException error) when (File.Exists(dialog.FileName))
        {
            Diagnostics.Exception("save-png", error);
            return "Файл с таким именем уже существует. Выберите другое имя.";
        }
        catch (Exception error)
        {
            return Describe(error, "save-png", "Не удалось сохранить PNG в выбранную папку.");
        }
    }

    private void SetRevitBusy(bool value)
    {
        revitBusy = value;
        UpdateControls();
        if (!value && refreshPending)
            QueueRefresh();
    }

    private void UpdateControls()
    {
        if (GenerateButton == null || HistoryBox == null || OpenSelectedButton == null || MaskHint == null)
            return;
        Views.IsEnabled = !revitBusy && viewList?.Views.Length > 0;
        ActiveButton.IsEnabled = !revitBusy && viewList?.ActiveId != null;
        PreviewButton.IsEnabled = !revitBusy;
        var local = IsMasked;
        MaskTools.Visibility = MaskCanvas.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        PreviewImage.Visibility = local ? Visibility.Hidden : Visibility.Visible;
        PreviewFrame.Height = local ? 230 : 150;
        ModeBox.IsEnabled = !revitBusy;
        MaskCanvas.IsEnabled = !revitBusy && !jobBusy;
        MaskToolBox.IsEnabled = ZoomBox.IsEnabled = !revitBusy && !jobBusy;
        MaskSizeBox.IsEnabled = !revitBusy && !jobBusy && MaskToolBox.SelectedIndex != 2;
        ClearMaskButton.IsEnabled = !revitBusy && !jobBusy && MaskCanvas.HasSelection;
        MaskHint.Text = MaskCanvas.HasSelection ? "" : "Отметьте участок, который нужно изменить.";
        MaskHint.Visibility = local && !MaskCanvas.HasSelection ? Visibility.Visible : Visibility.Collapsed;
        GenerateButton.IsEnabled = !jobBusy && !revitBusy && Views.SelectedItem is ViewChoice &&
            !string.IsNullOrWhiteSpace(PromptBox.Text) && (!local || MaskCanvas.HasSelection);
        CancelButton.Visibility = jobService != null ? Visibility.Visible : Visibility.Collapsed;
        PromptBox.IsReadOnly = jobBusy;
        PreserveBox.IsEnabled = !jobBusy;
        var job = HistoryBox.SelectedItem as RenderJob;
        ResumeButton.Visibility = job?.CanResume == true ? Visibility.Visible : Visibility.Collapsed;
        ResumeButton.IsEnabled = !jobBusy;
        OpenSelectedButton.Visibility = job?.State == "done" ? Visibility.Visible : Visibility.Collapsed;
        UpdateFooter();
    }

    private void UpdateFooter()
    {
        if (StatusFooter == null || GenerateButton == null || Tabs == null)
            return;
        var composing = Tabs.SelectedItem == NewTab;
        var hasStatus = JobStatus.Text.Length > 0 && (composing || jobBusy || Tabs.SelectedItem == HistoryTab);
        JobStatus.Visibility = hasStatus ? Visibility.Visible : Visibility.Collapsed;
        GenerateButton.Visibility = composing ? Visibility.Visible : Visibility.Collapsed;
        GenerateButton.Margin = hasStatus ? new Thickness(0, 7, 0, 0) : new Thickness(0);
        StatusFooter.Visibility = composing || hasStatus || jobService != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Describe(Exception error, string operation, string fallback)
    {
        Diagnostics.Exception(operation, error);
        return error is RenderException friendly ? friendly.Message : fallback;
    }
}
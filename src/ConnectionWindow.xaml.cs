using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace RevitAiRender;

public partial class ConnectionWindow : Window
{
    private readonly Settings settings;
    private readonly CancellationTokenSource close = new();

    internal ConnectionWindow(Settings settings)
    {
        this.settings = settings;
        InitializeComponent();
        TargetBox.Text = settings.SshTarget ?? "";
        SilicontrolBox.Text = settings.SilicontrolPath ?? Settings.DefaultCli;
        PythonBox.Text = settings.MfluxPython ?? "";
        CliBox.Text = settings.MfluxCli ?? "";
        ModelBox.Text = settings.MfluxModelPath ?? "";
        Closed += (_, _) => close.Cancel();
    }

    private void ReadFields()
    {
        settings.SshTarget = TargetBox.Text.Trim();
        settings.SilicontrolPath = Optional(SilicontrolBox.Text) ?? Settings.DefaultCli;
        settings.MfluxPython = Optional(PythonBox.Text);
        settings.MfluxCli = Optional(CliBox.Text);
        settings.MfluxModelPath = Optional(ModelBox.Text);
        settings.ValidateConnection(requireCompleteMflux: true);
    }

    private static string? Optional(string text)
    {
        var value = text.Trim();
        return value.Length == 0 ? null : value;
    }

    private void Save(object sender, RoutedEventArgs args)
    {
        try
        {
            ReadFields();
            settings.Save();
            DialogResult = true;
        }
        catch (RenderException error) { ShowMessage(error.Message); }
    }

    private async void CheckConnection(object sender, RoutedEventArgs args)
    {
        try
        {
            ReadFields();
            TestButton.IsEnabled = SaveButton.IsEnabled = false;
            TargetBox.IsReadOnly = SilicontrolBox.IsReadOnly = PythonBox.IsReadOnly =
                CliBox.IsReadOnly = ModelBox.IsReadOnly = true;
            ShowMessage("Проверяем SSH и Siliconed…");
            var message = await MacBridge.CheckConnectionAsync(settings, close.Token);
            if (!close.IsCancellationRequested)
                ShowMessage(message);
        }
        catch (OperationCanceledException) when (close.IsCancellationRequested) { }
        catch (RenderException error)
        {
            if (!close.IsCancellationRequested)
                ShowMessage(error.Message);
        }
        catch (Exception error)
        {
            Diagnostics.Exception("connection-check", error);
            if (!close.IsCancellationRequested)
                ShowMessage("Проверка связи не завершилась.");
        }
        finally
        {
            if (!close.IsCancellationRequested)
            {
                TestButton.IsEnabled = SaveButton.IsEnabled = true;
                TargetBox.IsReadOnly = SilicontrolBox.IsReadOnly = PythonBox.IsReadOnly =
                    CliBox.IsReadOnly = ModelBox.IsReadOnly = false;
            }
        }
    }

    private void FieldChanged(object sender, TextChangedEventArgs args)
    {
        if (Feedback != null)
            Feedback.Visibility = Visibility.Collapsed;
    }

    private void ShowMessage(string message)
    {
        Feedback.Text = message;
        Feedback.Visibility = Visibility.Visible;
    }
}

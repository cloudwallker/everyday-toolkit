using System.Windows;
using System.Windows.Threading;

/// <summary>Loads the real App.xaml and removes its queued personal-profile startup before any test pumps the dispatcher.</summary>
internal static class UiTestApplication
{
    public static EverydayToolkit.App.App Create()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var startupOperations = new List<DispatcherOperation>();
        void Capture(object? sender, DispatcherHookEventArgs e)
        {
            if (e.Operation.Priority == DispatcherPriority.Send) startupOperations.Add(e.Operation);
        }
        dispatcher.Hooks.OperationPosted += Capture;
        EverydayToolkit.App.App app;
        try { app = new EverydayToolkit.App.App(); }
        finally { dispatcher.Hooks.OperationPosted -= Capture; }
        if (startupOperations.Count != 1 || !startupOperations[0].Abort()) throw new Exception("test harness could not identify and suppress the sole application constructor startup operation");
        app.InitializeComponent();
        return app;
    }
}

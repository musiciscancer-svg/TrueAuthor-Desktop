using System.Windows.Automation;
namespace TrueAuthor.Desktop;

/// <summary>Observes accessible Paste menu actions; callers validate the approved target before reading the clipboard.</summary>
sealed class PasteActionMonitor : IDisposable
{
    readonly AutomationEventHandler handler;
    readonly Thread worker;
    readonly ManualResetEventSlim stop = new();
    public bool Available { get; private set; }
    public PasteActionMonitor(Action<int, uint> pasted, AutomationFocusChangedEventHandler focusChanged)
    {
        handler = (sender, _) =>
        {
            try
            {
                if (sender is not AutomationElement element) return;
                var name = element.Current.Name.Split('\t')[0].Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^Paste(?:$|\s+(?:as|and|special)\b)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return;
                pasted(element.Current.ProcessId, Native.GetClipboardSequenceNumber());
            }
            catch (ElementNotAvailableException) { }
            catch (System.Runtime.InteropServices.COMException) { }
        };
        // UIA subscriptions must be made off the app's own UI thread, or provider calls can deadlock startup.
        worker = new Thread(() =>
        {
            bool focusAdded = false, pasteAdded = false;
            try
            {
                Automation.AddAutomationFocusChangedEventHandler(focusChanged); focusAdded = true;
                Automation.AddAutomationEventHandler(InvokePattern.InvokedEvent, AutomationElement.RootElement, TreeScope.Descendants, handler); pasteAdded = true;
                Available = true; stop.Wait();
            }
            catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
            finally
            {
                Available = false;
                try
                {
                    if (pasteAdded) Automation.RemoveAutomationEventHandler(InvokePattern.InvokedEvent, AutomationElement.RootElement, handler);
                    if (focusAdded) Automation.RemoveAutomationFocusChangedEventHandler(focusChanged);
                }
                catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
            }
        }) { IsBackground = true, Name = "TrueAuthor accessibility events" };
        worker.SetApartmentState(ApartmentState.MTA); worker.Start();
    }
    public void Dispose() { stop.Set(); if (worker.Join(2000)) stop.Dispose(); }
}

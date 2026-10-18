using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Threading.Tasks;
using Clippboard.App.Design;
using Clippboard.App.Platform;
using Clippboard.App.Views;
using Clippboard.Core;

internal static partial class Program
{
    private static void CheckRetainedNotes(string root)
    {
        var prior = RenderOptions.ProcessRenderMode;
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var window = new RopeWindow(new HistoryStore(Path.Combine(root, "retained"), new TestWindowsProtector()), true,
            new AppSettings { Animate = false, PreviewCacheMiB = 64 })
            { Width = 1200, Height = 720, Left = -20000, Top = -20000, ShowActivated = false, Topmost = false };
        try
        {
            window.EnableNativeNotes(); window.Show(); window.RenderDemo(1000); window.DiagnosticFlushNativeNotes();
            Check(window.NativeNotesActive && window.NativeSurfaceBytes > 0, "retained note surfaces start without fallback: " + window.NativeFailure);
            var notes = (Canvas)window.FindName("Notes");
            var card = (Grid)notes.Children[0]; var select = (Button)card.Tag;
            Check(notes.Opacity == 0 && select.IsVisible && select.IsEnabled, "cached pixels preserve the original live control tree");
            Check((window.DiagnosticNativePixel(card, 30, 70) >> 24) > 240, "cached paper contains actual opaque pixels with a transparent WPF ancestor");
            var point = select.TranslatePoint(new Point(12, 12), window);
            var hit = window.InputHitTest(point) as DependencyObject;
            while (hit is not null && !ReferenceEquals(hit, select)) hit = VisualTreeHelper.GetParent(hit);
            Check(ReferenceEquals(hit, select), "transparent accessible notes remain hit-testable at their actual geometry");
            if (HorizontalWheel.ScrollCharacters() != 0)
            {
                var screen = window.PointToScreen(point);
                var packed = (nint)(((int)screen.Y & 0xffff) << 16 | ((int)screen.X & 0xffff));
                var native = window.NativeSurfaceHandles[0];
                SendMessage(native, HorizontalWheel.Message, 5 << 16, packed);
                Check(window.ScrollOffset > 0, "fractional horizontal wheel targeted at a cached window reaches the owning scroll controller");
                SendMessage(native, HorizontalWheel.Message, -5 << 16, packed);
                Check(window.ScrollOffset == 0, "native note wheel forwarding preserves direction and fractional reversals");
                window.DiagnosticFlushNativeNotes();
            }
            var peer = UIElementAutomationPeer.CreatePeerForElement(select)!;
            Check(peer.GetBoundingRectangle().Width > 0 && peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider, "cached note exposes its existing accessible bounds and invoke action");
            ClipSummary? restored = null; window.Selected += row => { restored = row; return Task.CompletedTask; };
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke(); Pump(Task.Delay(30));
            Check(restored is not null && System.Windows.Automation.AutomationProperties.GetName(select).Contains(restored.Preview), "accessible invoke restores the current summary through the existing handler");
            var captures = window.NativeSnapshotCount;
            for (var frame = 0; frame < 20; frame++) { window.DiagnosticScroll(frame * 3); window.DiagnosticFlushNativeNotes(); }
            Check(window.NativeSnapshotCount == captures, "moving cached notes does not rerasterize their text on every scroll update");
            Check(window.NativeNotesActive, "scroll retains native note surfaces: " + window.NativeFailure);
            window.DiagnosticScroll(0); window.DiagnosticFlushNativeNotes();
            window.SelectNote(1); window.DiagnosticFlushNativeNotes();
            Check(window.NativeSnapshotCount > captures, "selection invalidates cached note pixels");
            window.ApplyAppearance(window.SavedSettings with { Theme = AppTheme.Midnight }); window.DiagnosticFlushNativeNotes();
            var dark = window.DiagnosticNativePixel(card, 30, 70);
            window.ApplyAppearance(window.SavedSettings with { Theme = AppTheme.Cloud }); window.DiagnosticFlushNativeNotes();
            Check(dark != window.DiagnosticNativePixel(card, 30, 70), "theme changes replace cached colors rather than leaving stale notes");
            window.OpenSettings(); window.UpdateLayout();
            Check(window.NativeSurfaceBytes == 0 && notes.Opacity == 1, "settings restores live WPF notes below its panel and releases cached surfaces");
            var host = (Border)window.FindName("SidebarHost");
            var view = (SettingsView)host.Child; view.ShowPage(2);
            Descendants<Button>(view).First(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Note rendering: Hardware").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(view.Draft.EffectiveRendering == NoteRendering.Hardware && window.SavedSettings.EffectiveRendering == NoteRendering.Retained, "rendering choice edits only the draft until save and reopen");
            Descendants<Button>((SettingsView)host.Child).First(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Close settings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.DiagnosticFlushNativeNotes();
            Check(window.NativeNotesActive && window.NativeSurfaceBytes > 0 && notes.Opacity == 0, "closing the sidebar resumes cached notes");
            window.ApplyAppearance(window.SavedSettings with { NoteWidth = 400 }); window.DiagnosticFlushNativeNotes();
            Check(window.NativeNotesActive && window.RenderedNoteCount < 10, "note resizing replaces bounded buffers at current display scale");
            window.ApplyAppearance(window.SavedSettings with { Animate = true });
            window.AnimateEntrance(); Pump(Task.Delay(130)); window.DiagnosticFlushNativeNotes();
            Check(window.NativeNotesActive && (!SystemParameters.ClientAreaAnimation || window.NativeFrameRunning), "cached notes track entrance rotation or the Windows reduced-motion preference without fallback");
            if (SystemParameters.ClientAreaAnimation)
            {
                var retraction = window.RequestRetractionAsync(); Pump(Task.Delay(70)); Pump(window.ResumeRequestedOpenAsync()); Pump(retraction);
            }
            Pump(Task.Delay(SystemParameters.ClientAreaAnimation ? 2200 : 100));
            Check(window.NativeNotesActive && !window.NativeFrameRunning, "reversed retraction preserves cached notes and stops render callbacks at rest");
            var button = (Button)((Grid)notes.Children[0]).Tag;
            captures = window.NativeSnapshotCount;
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent }); Pump(Task.Delay(70));
            Pump(Task.Delay(300));
            Check(window.NativeSnapshotCount > captures, "animated button feedback refreshes the cached note during hover");
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent }); Pump(Task.Delay(1000));
            Check(!window.NativeFrameRunning, "hover completion releases the native rendering subscription");
            var handles = window.NativeSurfaceHandles;
            Pump(window.CloseRopeAsync());
            Check(window.NativeSurfaceBytes == 0 && window.NativeReservedBytes == 0 && !window.NativeFrameRunning, "retirement releases cached pixels and all frame subscriptions");
            Check(handles.Length > 0 && handles.All(handle => !NativeMethods.IsWindow(handle)), "retirement destroys every owned native note window without waiting for GC");
            var idleCaptures = window.NativeSnapshotCount;
            SmoothMotion.Move(new TranslateTransform(), TranslateTransform.XProperty, 2, false, 1); Pump(Task.Delay(30));
            Check(window.NativeSnapshotCount == idleCaptures && !window.NativeFrameRunning, "later unrelated motion cannot revive a closed note renderer");
            var pressure = new RopeWindow(new DeferredHistory(), true, new AppSettings { Animate = false })
                { Width = 1000, Height = 720, Left = -20000, Top = -20000, ShowActivated = false, Topmost = false };
            try
            {
                pressure.EnableNativeNotes(); pressure.Show(); pressure.RenderDemo(); pressure.DiagnosticFlushNativeNotes();
                var pressureHandles = pressure.NativeSurfaceHandles;
                Check(pressure.NativeNotesActive && pressureHandles.Length > 0, "pressure fixture starts with allocated note surfaces");
                pressure.DiagnosticNativePressure(0);
                Check(!pressure.NativeNotesActive && pressure.NativeReservedBytes == 0 && ((Canvas)pressure.FindName("Notes")).Opacity == 1
                    && pressure.RenderedNoteCount > 0 && !pressure.NativeFrameRunning && pressureHandles.All(handle => !NativeMethods.IsWindow(handle)),
                    "memory pressure releases actual native windows and restores the live compatible view without losing notes");
                pressure.DiagnosticFlushNativeNotes();
                Check(pressure.NativeSurfaceBytes == 0, "compatible fallback does not retry allocation in a rendering loop");
            }
            finally { pressure.Close(); }
            pressure = new RopeWindow(new DeferredHistory(), true, new AppSettings { Animate = false })
                { Width = 1000, Height = 720, Left = -20000, Top = -20000, ShowActivated = false, Topmost = false };
            try
            {
                pressure.DiagnosticNativePressure(0); pressure.EnableNativeNotes(); pressure.Show(); pressure.RenderDemo(); pressure.DiagnosticFlushNativeNotes();
                Check(!pressure.NativeNotesActive && pressure.NativeSurfaceBytes == 0 && pressure.RenderedNoteCount > 0
                    && ((Canvas)pressure.FindName("Notes")).Opacity == 1, "insufficient initial headroom never leaves transparent blank notes");
            }
            finally { pressure.Close(); }
        }
        finally { window.Close(); RenderOptions.ProcessRenderMode = prior; }
        Check(MemoryAvailability.RetainedSurfaceBudget(400, 10000) == 400
            && MemoryAvailability.RetainedSurfaceBudget(400, 1000) == 40
            && MemoryAvailability.RetainedSurfaceBudget(40, -1) == 40
            && MemoryAvailability.RetainedSurfaceBudget(40, 0) == 0,
            "cached note admission respects viewport demand and current headroom");
    }
}

// Drag-and-drop gestures for OpenHarmony: the platform delivers raw touches, so a press held
// within a small slop and then moved is promoted to a MAUI drag. While dragging, the drop target
// under the pointer receives DragOver (and DragLeave when it changes) and the release delivers
// Drop with the DataPackage produced by the drag source, followed by DropCompleted on the source.
//
// The recognizers' dispatch members (DragGestureRecognizer.SendDragStarting/SendDropCompleted and
// DropGestureRecognizer.SendDragOver/SendDragLeave/SendDrop) are public in the Controls contract,
// so they are called directly; every call is still guarded because a failing recognizer handler
// must never take the touch pipeline or the frame loop down.
//
// Payloads: the session carries the DataPackage the source's DragStarting handler produced and
// Drop hands the target the same package through DropEventArgs.Data (a DataPackageView), so
// text, image (DataPackage.Image, read back with GetImageAsync) and custom Properties payloads
// survive the simulated session. MAUI 11's DataPackage (Microsoft.Maui.Controls) exposes Text,
// Image and Properties only - there is no typed file/URI member and DataPackageView has no
// GetFileAsync - so a file or URI cannot be expressed by the Controls contract at all; this
// slice also has no OS drag session (the gesture is simulated from touches), so nothing could
// carry one. Nothing is dropped silently on this side: an image/property payload reaches the
// drop target as-is, and a package without text only gains the source-text fallback below.
// A URI/file can still ride as Text or as a custom Properties value, which the drop side reads
// through DataPackageView.GetTextAsync/TryGetValue.
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonyDragAndDrop
{
    /// <summary>Press time (ms) that promotes a held touch into a drag once it moves.</summary>
    public const long LongPressMilliseconds = 500;

    /// <summary>Movement (px) allowed while the press is held; a move beyond it starts the drag.</summary>
    public const float Slop = 8f;

    /// <summary>One in-flight drag: its source recognizers, DataPackage and current drop target.</summary>
    internal sealed class Session
    {
        public Session(DataPackage package, List<DragGestureRecognizer> recognizers)
        {
            Package = package;
            Recognizers = recognizers;
        }

        public DataPackage Package { get; }

        public List<DragGestureRecognizer> Recognizers { get; }

        public IView? Target { get; set; }
    }

    /// <summary>True when the view owns a drag recognizer that accepts drags.</summary>
    public static bool HasDrag(IView view)
        => view is View controlsView &&
           controlsView.GestureRecognizers.OfType<DragGestureRecognizer>().Any(recognizer => recognizer.CanDrag);

    /// <summary>True when the view owns a drop recognizer that accepts drops.</summary>
    public static bool HasDrop(IView view)
        => view is View controlsView &&
           controlsView.GestureRecognizers.OfType<DropGestureRecognizer>().Any(recognizer => recognizer.AllowDrop);

    /// <summary>
    /// Raises DragStarting on the source's recognizers. Returns a session (with the DataPackage of
    /// the first accepting recognizer) or null when every recognizer cancelled.
    /// </summary>
    public static Session? Start(IView source, float x, float y)
    {
        if (source is not View controlsView)
        {
            return null;
        }
        var recognizers = controlsView.GestureRecognizers
            .OfType<DragGestureRecognizer>()
            .Where(recognizer => recognizer.CanDrag)
            .ToList();
        if (recognizers.Count == 0)
        {
            return null;
        }
        DataPackage? package = null;
        var started = new List<DragGestureRecognizer>();
        foreach (DragGestureRecognizer recognizer in recognizers)
        {
            DragStartingEventArgs args;
            try
            {
                args = recognizer.SendDragStarting(controlsView, _ => new Point(x, y), null!);
            }
            catch (Exception)
            {
                // Ignored: a handler failure must not break the touch pipeline.
                continue;
            }
            if (args.Cancel)
            {
                continue;
            }
            started.Add(recognizer);
            package ??= args.Data;
        }
        if (started.Count == 0)
        {
            return null;
        }
        package ??= new DataPackage();
        if (string.IsNullOrEmpty(package.Text))
        {
            // The handler did not produce text: fall back to the source's text or automation id.
            package.Text = TextFor(source);
        }
        return new Session(package, started);
    }

    /// <summary>Moves the drag: the previous target gets DragLeave and the new one DragOver.</summary>
    public static void Update(Session session, IView? target)
    {
        if (ReferenceEquals(session.Target, target))
        {
            return;
        }
        IView? previous = session.Target;
        session.Target = target;
        if (previous is not null)
        {
            Dispatch(previous, recognizer => recognizer.SendDragLeave(new DragEventArgs(session.Package)));
        }
        if (target is not null)
        {
            Dispatch(target, recognizer => recognizer.SendDragOver(new DragEventArgs(session.Package)));
        }
    }

    /// <summary>Releases the drag: the target (when any) receives Drop, then the source DropCompleted.</summary>
    public static void Drop(Session session, IView? target)
    {
        Update(session, target);
        if (target is not null)
        {
            Dispatch(target, recognizer =>
            {
                Task task = recognizer.SendDrop(new DropEventArgs(session.Package.View));
                // Observe asynchronous handler failures without blocking the frame loop.
                task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            });
        }
        Complete(session);
    }

    /// <summary>Ends the drag without a drop (pointer owner changed): DragLeave, then DropCompleted.</summary>
    public static void Cancel(Session session)
    {
        Update(session, null);
        Complete(session);
    }

    private static void Complete(Session session)
    {
        foreach (DragGestureRecognizer recognizer in session.Recognizers)
        {
            try
            {
                recognizer.SendDropCompleted(new DropCompletedEventArgs());
            }
            catch (Exception)
            {
                // Ignored: a handler failure must not break the touch pipeline.
            }
        }
    }

    private static void Dispatch(IView view, Action<DropGestureRecognizer> action)
    {
        if (view is not View controlsView)
        {
            return;
        }
        foreach (DropGestureRecognizer recognizer in controlsView.GestureRecognizers.OfType<DropGestureRecognizer>())
        {
            if (!recognizer.AllowDrop)
            {
                continue;
            }
            try
            {
                action(recognizer);
            }
            catch (Exception)
            {
                // Ignored: a handler failure must not break the touch pipeline.
            }
        }
    }

    /// <summary>Package text: the source's IText/ILabel text, else its automation id, else null.</summary>
    private static string? TextFor(IView source)
    {
        if (source is IText text && !string.IsNullOrEmpty(text.Text))
        {
            return text.Text;
        }
        if (source is ILabel label && !string.IsNullOrEmpty(label.Text))
        {
            return label.Text;
        }
        if (source is View view && !string.IsNullOrEmpty(view.AutomationId))
        {
            return view.AutomationId;
        }
        return null;
    }
}

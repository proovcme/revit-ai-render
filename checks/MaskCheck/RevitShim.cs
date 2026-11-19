using System;
using System.Threading.Tasks;
using System.Windows;

namespace RevitAiRender
{
    internal sealed class RevitViewService
    {
        internal Task<ViewList> ListAsync() => Task.FromResult(new ViewList());
        internal Task<string> ExportAsync(ViewChoice _, string dir, int size) => throw new NotSupportedException();
        internal Task<ElementFrames> ExportElementFramesAsync(ViewChoice _, string dir, int size, string[]? ids = null) => throw new NotSupportedException();
    }

    internal sealed class ViewList
    {
        internal string Project => "Mask check";
        internal ViewChoice[] Views => new[] { new ViewChoice() };
        internal string? ActiveId => "fixture";
    }

    internal sealed class ViewChoice
    {
        internal int Document => 1;
        internal string Project => "Mask check";
        internal string Name => "Fixture";
        internal string UniqueId => "fixture";
        internal bool Perspective => false;
    }
}

namespace Autodesk.Revit.UI
{
    public enum DockPosition { Right }
    public sealed class DockablePaneState { public DockPosition DockPosition { get; set; } }
    public sealed class DockablePaneProviderData
    {
        public FrameworkElement? FrameworkElement { get; set; }
        public DockablePaneState? InitialState { get; set; }
    }
    public interface IDockablePaneProvider { void SetupDockablePane(DockablePaneProviderData data); }
}

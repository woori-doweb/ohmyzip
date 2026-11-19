using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace OhMyPic.App.Editor;

/// <summary>
/// The crop frame layer. A plain <see cref="Canvas"/> has no automation peer, so the frame would be missing from the UIA
/// tree; this layer reports itself as a pane with its AutomationProperties name and help text (the current crop size).
/// </summary>
public sealed class CropOverlayCanvas : Canvas
{
    protected override AutomationPeer OnCreateAutomationPeer() => new CropOverlayAutomationPeer(this);

    private sealed class CropOverlayAutomationPeer(CropOverlayCanvas owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;

        protected override string GetClassNameCore() => nameof(CropOverlayCanvas);
    }
}

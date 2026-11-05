namespace OhMyPic.App.Models;

/// <summary>One label/value line of the camera info pane.</summary>
public sealed record InfoRow(string Label, string Value)
{
    /// <summary>Single accessible name for screen readers ("Camera model: X100").</summary>
    public string Accessible => Label + ": " + Value;
}

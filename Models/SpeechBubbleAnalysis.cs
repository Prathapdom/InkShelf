namespace InkShelf.Models;

public sealed class SpeechBubblePage
{
    public int Version { get; set; } = 2;
    public int PageIndex { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public DateTimeOffset AnalyzedAt { get; set; } = DateTimeOffset.Now;
    public List<SpeechBubbleRegion> Bubbles { get; set; } = [];
    public List<ComicPanelRegion> Panels { get; set; } = [];
}

public sealed class ComicPanelRegion
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Confidence { get; set; }
    public int Order { get; set; }
}

public sealed class SpeechBubbleRegion
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Confidence { get; set; }
    public int Order { get; set; }
}

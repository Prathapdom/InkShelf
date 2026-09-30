namespace InkShelf.Models;

public sealed class PageTranslation
{
    public int PageIndex { get; set; }
    public string JapaneseText { get; set; } = string.Empty;
    public string EnglishText { get; set; } = string.Empty;
    public DateTimeOffset TranslatedAt { get; set; } = DateTimeOffset.Now;
}

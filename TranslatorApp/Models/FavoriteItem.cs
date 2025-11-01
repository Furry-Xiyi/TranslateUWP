using System;

namespace TranslatorApp.Models;

public class FavoriteItem
{
    public string Term { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty; // 可留空
    public DateTime AddedOn { get; set; } = DateTime.Now;
}
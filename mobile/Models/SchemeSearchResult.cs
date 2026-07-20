namespace CarDiagnosticApp.Models;

/// <summary>
/// Результат поиска схем в интернете.
/// </summary>
public class SchemeSearchResult
{
    public string error_code { get; set; } = "";
    public string car_brand { get; set; } = "";
    public string car_model { get; set; } = "";
    public string search_engine { get; set; } = "";
    public int query_count { get; set; }
    public List<SchemeSearchItem> results { get; set; } = new();
    public int total_found { get; set; }
    public string query { get; set; } = "";
}

/// <summary>
/// Один элемент результатов поиска схемы.
/// </summary>
public class SchemeSearchItem
{
    public string title { get; set; } = "";
    public string url { get; set; } = "";
    public string snippet { get; set; } = "";
    public string thumbnail { get; set; } = "";
    public string image_url { get; set; } = "";
    public string full_image_url { get; set; } = "";
    public string source { get; set; } = "";
    public string page_url { get; set; } = "";
}

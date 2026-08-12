namespace CarDiagnosticApp.Models;

/// <summary>Один монитор готовности OBD-II (Mode 01 PID 01).</summary>
public class ReadinessMonitor
{
    public string Name { get; set; } = "";
    public bool Supported { get; set; }
    public bool Complete { get; set; }
    public string StatusText => !Supported ? "не поддерживается" : Complete ? "готов" : "не завершён";
}

/// <summary>Статус готовности: лампа MIL, число ошибок, мониторы.</summary>
public class ReadinessStatus
{
    public bool MilOn { get; set; }
    public int DtcCount { get; set; }
    public bool IsDiesel { get; set; }
    public List<ReadinessMonitor> Monitors { get; set; } = new();
}

namespace EveConsole.Models;

public class ApiTimerSetting
{
    public string Key             { get; set; } = "";
    public int    IntervalSeconds { get; set; }
}

public class AppPreference
{
    public string Key   { get; set; } = "";
    public string Value { get; set; } = "";
}

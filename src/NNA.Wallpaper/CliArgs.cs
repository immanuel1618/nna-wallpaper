namespace NNA.Wallpaper;

/// <summary>Command-line flags (see docs: --settings --reload --pause --resume --import --exit --port --data --headless).</summary>
public sealed class CliArgs
{
    public bool Headless { get; private set; }
    public bool TestEngine { get; private set; }
    public bool Settings { get; private set; }
    public bool Reload { get; private set; }
    public bool Pause { get; private set; }
    public bool Resume { get; private set; }
    public bool Exit { get; private set; }
    public bool DevTools { get; private set; }
    public bool CheckUpdates { get; private set; }
    public string? Import { get; private set; }
    public string? DataDir { get; private set; }
    public int? Port { get; private set; }
    public List<string> Unknown { get; } = new();

    public static CliArgs Parse(string[] args)
    {
        var a = new CliArgs();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (arg.ToLowerInvariant())
            {
                case "--headless": a.Headless = true; break;
                case "--test-engine": a.TestEngine = true; break;
                case "--settings": a.Settings = true; break;
                case "--reload": a.Reload = true; break;
                case "--pause": a.Pause = true; break;
                case "--resume": a.Resume = true; break;
                case "--exit": a.Exit = true; break;
                case "--devtools": a.DevTools = true; break;
                case "--check-updates": a.CheckUpdates = true; break;
                case "--import": a.Import = Next(); break;
                case "--data": a.DataDir = Next(); break;
                case "--port":
                    if (int.TryParse(Next(), out var p)) a.Port = p;
                    break;
                default: a.Unknown.Add(arg); break;
            }
        }
        return a;
    }
}

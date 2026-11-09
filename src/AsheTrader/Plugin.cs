using AsheTrader.Integrations;
using AsheTrader.Core;
using AsheTrader.Fuel;
using AsheTrader.Trading;
using AsheTrader.Windows;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using AsheTrader.Workflow;

namespace AsheTrader;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;
    [PluginService] public static ICondition Condition { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;

    public Configuration Config { get; private set; }
    public TradeRunner Trader { get; }
    internal AutoRetainerClient AutoRetainer { get; }
    internal LifestreamClient Lifestream { get; }
    public FuelRunner Fuel { get; }
    public WorkflowService Workflow { get; }
    public string SettingsError { get; private set; } = string.Empty;
    public Queue<string> Activity { get; } = new();
    private readonly WindowSystem windows = new("AsheTrader");
    private readonly MainWindow mainWindow;
    private string lastStatus = string.Empty;
    private string lastFuelStatus = string.Empty;
    private string lastWorkflowStatus = string.Empty;
    private long nextArCheck;
    private readonly SettingsFileStore settings;
    private bool settingsReadable = true;
    private bool disposed;

    public Plugin()
    {
        settings = new SettingsFileStore(Path.Combine(PluginInterface.ConfigDirectory.FullName, "settings.json"));
        Config = LoadSettings();
        Trader = new TradeRunner();
        AutoRetainer = new AutoRetainerClient(PluginInterface, Framework, Log);
        Lifestream = new LifestreamClient();
        Fuel = new FuelRunner();
        Workflow = new WorkflowService(this);
        Save();
        mainWindow = new MainWindow(this) { IsOpen = Config.OpenOnLoad };
        windows.AddWindow(mainWindow);
        CommandManager.AddHandler("/ashetrader", new CommandInfo(OnCommand)
        { HelpMessage = "Open Ashe's Trader. Use /ashetrader stop to stop the current run." });
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += Open;
        PluginInterface.UiBuilder.OpenConfigUi += Open;
        Framework.Update += Update;
        ClientState.Logout += OnLogout;
        Record("Loaded. No trading or purchasing starts automatically.");
    }

    private Configuration LoadSettings()
    {
        try
        {
            var json = settings.Load();
            var loaded = json == null ? new Configuration() : JsonConvert.DeserializeObject<Configuration>(json)
                ?? throw new InvalidDataException("Settings were empty.");
            loaded.Normalize();
            settingsReadable = true;
            return loaded;
        }
        catch (Exception ex)
        {
            settingsReadable = false;
            SettingsError = $"Settings could not be read and will not be overwritten: {ex.Message}";
            Log.Error(ex, "Settings load failed");
            return new Configuration();
        }
    }

    public void Save()
    {
        if (!settingsReadable) return;
        if (settings.TrySave(JsonConvert.SerializeObject(Config, Formatting.Indented), out var error))
        {
            SettingsError = string.Empty;
            return;
        }
        Config = LoadSettings();
        SettingsError = error + " Latest settings were reloaded; reapply your last edit.";
    }

    public void ReloadSettings()
    {
        SettingsError = string.Empty;
        Config = LoadSettings();
    }

    public void StopAll() { Trader.Stop(); Fuel.Stop(); Workflow.Pause("Paused by user."); }
    public void Open() => mainWindow.IsOpen = true;
    public void Record(string message)
    {
        Workflow.RecordActivity(message);
        Activity.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
        while (Activity.Count > 100) Activity.Dequeue();
        Log.Information(message);
    }

    public void QueueAction(Action action)
    {
        // UI and framework callbacks must never race native game state.
        _ = Framework.RunOnFrameworkThread(() =>
        {
            if (disposed) return;
            try { action(); }
            catch (Exception ex) { Record($"Stopped: {ex.Message}"); StopAll(); Log.Error(ex, "Operation failed"); }
        });
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase)) QueueAction(StopAll);
        else Open();
    }

    private void Update(IFramework framework)
    {
        try
        {
            if ((Trader.IsRunning || Fuel.Active) && Environment.TickCount64 >= nextArCheck)
            {
                nextArCheck = Environment.TickCount64 + 1000;
                var ar = AutoRetainer.GetStatus();
                if (ar.Ready && (ar.Busy != false || ar.MultiModeEnabled != false))
                { StopAll(); Record("Stopped because AutoRetainer resumed or its idle state could not be verified."); }
            }
            Trader.Update();
            Fuel.Update();
            Workflow.Update();
        }
        catch (Exception ex) { StopAll(); Record($"Operation stopped: {ex.Message}"); Log.Error(ex, "Runner update failed"); }
        if (lastStatus != Trader.Status) { lastStatus = Trader.Status; Record(lastStatus); }
        if (lastFuelStatus != Fuel.Status) { lastFuelStatus = Fuel.Status; Record(lastFuelStatus); }
        if (lastWorkflowStatus != Workflow.Status) { lastWorkflowStatus = Workflow.Status; Record(lastWorkflowStatus); }
    }

    private void OnLogout(int type, int code)
    {
        Trader.Stop(); Fuel.Stop();
        if (!Workflow.ExpectsLogout) Workflow.Pause("Unexpected logout; job paused.");
    }

    public void Dispose()
    {
        disposed = true;
        Framework.Update -= Update;
        ClientState.Logout -= OnLogout;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= Open;
        PluginInterface.UiBuilder.OpenConfigUi -= Open;
        CommandManager.RemoveHandler("/ashetrader");
        Trader.Dispose();
        Workflow.Dispose();
        Fuel.Stop();
        windows.RemoveAllWindows();
    }
}

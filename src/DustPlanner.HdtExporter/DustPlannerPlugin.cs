using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Controls;
using DustPlanner.HdtExporter.Export;
using DustPlanner.HdtExporter.Hdt;
using Hearthstone_Deck_Tracker.Plugins;

namespace DustPlanner.HdtExporter;

/// <summary>
/// HDT plugin entry point. Read-only by design: it never sends input to the game, never reads game
/// memory on its own, and only consumes data HDT already holds. See SECURITY.md.
/// </summary>
public sealed class DustPlannerPlugin : IPlugin
{
    private static readonly string[] ExportModes = { "HUB", "TOURNAMENT", "COLLECTIONMANAGER", "PACKOPENING" };

    private IHdtGateway? _hdt;
    private ExporterSettings? _settings;
    private ExportService? _service;
    private MenuItem? _menu;
    private MenuItem? _statusItem;
    private bool _collectionHooked;
    private bool _modeHooked;

    public string Name => "DustPlanner Exporter";

    public string Description =>
        "Exports your collection, decks and rank to JSON files for DustPlanner. Read-only: never automates or touches the game client.";

    public string ButtonText => "Export now";

    public string Author => "DustPlanner contributors";

    public Version Version => typeof(DustPlannerPlugin).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public MenuItem MenuItem => _menu ??= BuildMenu();

    public void OnLoad()
    {
        try
        {
            var root = ExporterSettings.DefaultRootDirectory;
            PluginLog.Initialize(root);
            _settings = ExporterSettings.LoadOrCreate(root);
            PluginLog.Initialize(_settings.ResolvedRootDirectory);

            var gateway = new HdtGateway();
            _hdt = gateway;
            _service = new ExportService(gateway, _settings);

            _collectionHooked = gateway.SubscribeCollectionChanged(() => RunInBackground("collection_changed", manual: false));
            _modeHooked = gateway.SubscribeModeChanged(OnModeChanged);

            PluginLog.Info("Loaded v" + Version + " (HDT " + (gateway.HdtVersion?.ToString() ?? "?") + ", HearthMirror " + (gateway.HearthMirrorVersion?.ToString() ?? "?") + "). " +
                           "collection event: " + (_collectionHooked ? "on" : "OFF") + ", mode event: " + (_modeHooked ? "on" : "OFF") + ". Inbox: " + _settings.InboxDirectory);
            UpdateStatus(_collectionHooked ? "Waiting for collection changes" : "Collection event unavailable in this HDT version; use Export now");
        }
        catch (Exception ex)
        {
            PluginLog.Error("OnLoad failed.", ex);
            UpdateStatus("Failed to load: " + ex.Message);
        }
    }

    public void OnUnload()
    {
        // Event handlers cannot be detached from HDT's ActionList API; the service simply stops when HDT exits.
        PluginLog.Info("Unloaded");
    }

    public void OnButtonPress() => RunInBackground("manual", manual: true);

    public void OnUpdate()
    {
        // Nothing periodic: exports are event-driven.
    }

    private void OnModeChanged(string mode)
    {
        if (_settings?.ExportOnModeChange != true || Array.IndexOf(ExportModes, mode) < 0)
        {
            return;
        }

        RunInBackground("mode_changed:" + mode, manual: false);
    }

    private void RunInBackground(string trigger, bool manual)
    {
        var service = _service;
        if (service is null)
        {
            UpdateStatus("Not initialised");
            return;
        }

        Task.Run(() =>
        {
            var status = service.ExportAll(trigger, manual);
            UpdateStatus(status);
        });
    }

    private void RunDiagnosticsInBackground()
    {
        var service = _service;
        if (service is null)
        {
            UpdateStatus("Not initialised");
            return;
        }

        Task.Run(() => UpdateStatus(service.ExportDiagnostics()));
    }

    private MenuItem BuildMenu()
    {
        var menu = new MenuItem { Header = "DustPlanner" };

        var exportNow = new MenuItem { Header = "Export collection, decks and rank now" };
        exportNow.Click += (_, _) => RunInBackground("manual", manual: true);
        menu.Items.Add(exportNow);

        var diagnostics = new MenuItem { Header = "Export diagnostics (premium slots / Core set)" };
        diagnostics.Click += (_, _) => RunDiagnosticsInBackground();
        menu.Items.Add(diagnostics);

        var openFolder = new MenuItem { Header = "Open export folder" };
        openFolder.Click += (_, _) => OpenInboxFolder();
        menu.Items.Add(openFolder);

        menu.Items.Add(new Separator());
        _statusItem = new MenuItem { Header = "Status: " + (_service?.LastStatus ?? "not loaded"), IsEnabled = false };
        menu.Items.Add(_statusItem);

        return menu;
    }

    private void OpenInboxFolder()
    {
        var dir = _settings?.InboxDirectory;
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(dir);
            using (Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true }))
            {
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error("Could not open " + dir, ex);
        }
    }

    private void UpdateStatus(string status)
    {
        var item = _statusItem;
        if (item is null)
        {
            return;
        }

        try
        {
            item.Dispatcher.BeginInvoke(new Action(() => item.Header = "Status: " + status));
        }
        catch (Exception ex)
        {
            PluginLog.Warn("Could not update status: " + ex.Message);
        }
    }
}

using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Controls;
using DustPlanner.HdtExporter.Export;
using DustPlanner.HdtExporter.Hdt;
using DustPlanner.HdtExporter.Service;
using Hearthstone_Deck_Tracker.Plugins;

namespace DustPlanner.HdtExporter;

/// <summary>
/// HDT plugin entry point. Read-only by design: it never sends input to the game, never reads game
/// memory on its own, and only consumes data HDT already holds. See SECURITY.md.
/// Besides exporting, it starts the DustPlanner service (panel, overlay, automatic sync) and opens the panel.
/// </summary>
public sealed class DustPlannerPlugin : IPlugin, IDisposable
{
    private static readonly string[] ExportModes = { "HUB", "TOURNAMENT", "COLLECTIONMANAGER", "PACKOPENING" };

    private HdtGateway? _hdt;
    private ExporterSettings? _settings;
    private ExportService? _service;
    private ServiceLauncher? _launcher;
    private MenuItem? _menu;
    private MenuItem? _statusItem;
    private MenuItem? _serviceItem;
    private bool _collectionHooked;
    private bool _modeHooked;
    private bool _gameStartHooked;
    private bool _activeDeckHooked;

    public string Name => "DustPlanner";

    public string Description =>
        "Dust planner for Hearthstone: what you are missing for a deck and what to disenchant for it, with your collection exported from HDT. Read-only: never automates or touches the game client.";

    public string ButtonText => "Open DustPlanner";

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
            _gameStartHooked = gateway.SubscribeGameStart(() => RunActiveDeckInBackground("game_start"));
            _activeDeckHooked = gateway.SubscribeActiveDeckChanged(() => RunActiveDeckInBackground("active_deck_changed"));

            PluginLog.Info("Loaded v" + Version + " (HDT " + (gateway.HdtVersion?.ToString() ?? "?") + ", HearthMirror " + (gateway.HearthMirrorVersion?.ToString() ?? "?") + "). " +
                           "collection event: " + (_collectionHooked ? "on" : "OFF") + ", mode event: " + (_modeHooked ? "on" : "OFF") +
                           ", game start event: " + (_gameStartHooked ? "on" : "OFF") + ", active deck event: " + (_activeDeckHooked ? "on" : "OFF") + ". Inbox: " + _settings.InboxDirectory);
            UpdateStatus(_collectionHooked ? "Waiting for collection changes" : "Collection event unavailable in this HDT version; use Export now");

            if (_settings.StartService)
            {
                _launcher = new ServiceLauncher(_settings.ResolvedRootDirectory, _settings.ServicePort, _settings.ServicePath);
                _launcher.StartInBackground(UpdateServiceStatus);
            }
            else
            {
                UpdateServiceStatus("Service disabled in exporter-settings.json");
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error("OnLoad failed.", ex);
            UpdateStatus("Failed to load: " + ex.Message);
        }
    }

    public void OnUnload()
    {
        // Event handlers cannot be detached from HDT's ActionList API; the export service simply stops when HDT exits.
        _launcher?.Stop();
        PluginLog.Info("Unloaded");
    }

    public void OnButtonPress() => OpenPanel(null);

    public void Dispose() => _launcher?.Dispose();

    public void OnUpdate()
    {
        // Nothing periodic: exports are event-driven and the service polls on its own.
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

    /// <summary>Only the active deck: cheap (no memory read) and content-hashed, so firing it on every game start is fine.</summary>
    private void RunActiveDeckInBackground(string trigger)
    {
        var service = _service;
        if (service is null)
        {
            return;
        }

        Task.Run(() => UpdateStatus(service.ExportActiveDeckOnly(trigger)));
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

    /// <summary>Opens the panel in the default browser, on the Deck page when a deck code is given.</summary>
    private void OpenPanel(string? deckstring)
    {
        var url = _launcher?.Url;
        if (url is null)
        {
            UpdateServiceStatus(_launcher?.Status ?? "Service not started; enable start_service in exporter-settings.json");
            return;
        }

        var target = deckstring is null ? url + "/" : url + "/#/deck?code=" + Uri.EscapeDataString(deckstring);
        try
        {
            using (Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }))
            {
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error("Could not open " + target, ex);
            UpdateServiceStatus("Could not open the browser: " + ex.Message);
        }
    }

    private void OpenPanelForSelectedDeck()
    {
        var deck = _hdt?.GetActiveDeck();
        if (deck is null || string.IsNullOrEmpty(deck.Deckstring))
        {
            UpdateServiceStatus("Select a deck in HDT's deck list first");
            OpenPanel(null);
            return;
        }

        OpenPanel("### " + deck.Name + "\n" + deck.Deckstring);
    }

    private MenuItem BuildMenu()
    {
        var menu = new MenuItem { Header = "DustPlanner" };

        var open = new MenuItem { Header = "Open DustPlanner" };
        open.Click += (_, _) => OpenPanel(null);
        menu.Items.Add(open);

        var selected = new MenuItem { Header = "What am I missing for the selected deck?" };
        selected.Click += (_, _) => OpenPanelForSelectedDeck();
        menu.Items.Add(selected);

        menu.Items.Add(new Separator());

        var exportNow = new MenuItem { Header = "Export collection, decks, rank and active deck now" };
        exportNow.Click += (_, _) => RunInBackground("manual", manual: true);
        menu.Items.Add(exportNow);

        var diagnostics = new MenuItem { Header = "Export diagnostics (premium slots / Core set)" };
        diagnostics.Click += (_, _) => RunDiagnosticsInBackground();
        menu.Items.Add(diagnostics);

        var openFolder = new MenuItem { Header = "Open export folder" };
        openFolder.Click += (_, _) => OpenInboxFolder();
        menu.Items.Add(openFolder);

        menu.Items.Add(new Separator());
        _serviceItem = new MenuItem { Header = "Service: " + (_launcher?.Status ?? "not started"), IsEnabled = false };
        menu.Items.Add(_serviceItem);
        _statusItem = new MenuItem { Header = "Export: " + (_service?.LastStatus ?? "not loaded"), IsEnabled = false };
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

    private void UpdateStatus(string status) => SetHeader(_statusItem, "Export: " + status);

    private void UpdateServiceStatus(string status) => SetHeader(_serviceItem, "Service: " + status);

    private static void SetHeader(MenuItem? item, string header)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            item.Dispatcher.BeginInvoke(new Action(() => item.Header = header));
        }
        catch (Exception ex)
        {
            PluginLog.Warn("Could not update status: " + ex.Message);
        }
    }
}

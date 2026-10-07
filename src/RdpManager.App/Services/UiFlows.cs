using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using RdpManager.App.ViewModels;
using RdpManager.App.Views;
using RdpManager.App.Views.Dialogs;
using RdpManager.Core.Json;
using RdpManager.Core.Models;
using RdpManager.Core.Validation;
using RdpManager.Infrastructure.Logging;
using RdpManager.Infrastructure.Migration;

namespace RdpManager.App.Services;

/// <summary>
/// User flows that combine dialogs and controller calls (port of dialogs.js and the actions in app.js).
/// Runs on the UI thread; long work is awaited, never blocking the dispatcher.
/// </summary>
public sealed class UiFlows(AppController controller, MainViewModel vm)
{
    public TrayService? Tray { get; set; }
    private ToastService Toasts => vm.Toasts;
    private AppSettings Settings => controller.Store.Settings;
    private bool TabsMode => Settings.SessionWindow != "external";

    public void Error(string title, string message, string? technical = null) => DialogWindow.Error(title, message, technical);

    private static void Run(Action action, string title = "The action could not be completed")
    {
        try { action(); }
        catch (Exception e) when (e is InvalidOperationException or ValidationException or IOException or UnauthorizedAccessException) { DialogWindow.Error(title, e.Message); }
    }

    // ── Systems ──────────────────────────────────────────
    public void AddSystem() => OpenEdit(null);

    public void Edit(ConnectionItem? item)
    {
        if (item is null || item.IsCentral) return;
        OpenEdit(item.Model);
    }

    public void Duplicate(ConnectionItem? item)
    {
        if (item is null) return;
        var copy = item.Model.Clone();
        copy.Id = "";
        copy.Name = $"{item.Name} (copy)";
        copy.Favorite = false;
        copy.Sample = false;
        copy.LastConnectedAt = null;
        copy.Source = null;
        OpenEdit(copy);
    }

    private void OpenEdit(Connection? existing)
    {
        if (!vm.CanQuickConnect) return;
        string? savedUser = null;
        var isExisting = existing is not null && !string.IsNullOrEmpty(existing.Id);
        if (isExisting)
        {
            try { var cred = controller.GetCredential(existing!.Id); if (cred.Saved) savedUser = cred.Username; }
            catch (InvalidOperationException) { }
        }
        var model = new EditSystemViewModel(existing, Settings, controller.Policy, vm.Items.Select(i => i.Folder).Where(f => f.Length > 0), savedUser);
        var view = new EditSystemView(model);
        var dlg = new DialogWindow(model.Title, null, view, 760);
        dlg.AddButton("Cancel", "Btn.Secondary", dlg.Close, isCancel: true);
        dlg.AddButton(model.SaveLabel, "Btn.Primary", () =>
        {
            var payload = model.BuildPayload(out var general);
            if (payload is null) { view.FocusProblem(general); return; }
            var password = model.IsRdp ? view.Password : "";
            if (password.Length > 0 && payload.Username.Length == 0)
            {
                model.FormError = "Enter the username that belongs to the password.";
                return;
            }
            Connection saved;
            try { saved = controller.Save(payload); }
            catch (Exception e) when (e is ValidationException or InvalidOperationException) { model.FormError = e.Message; return; }
            if (password.Length > 0)
            {
                try { controller.SaveCredential(saved.Id, payload.Username, password); }
                catch (InvalidOperationException e)
                {
                    // The system is saved; only the password failed. Keep the dialog open to say so.
                    model.FormError = $"The system was saved, but the password could not be saved: {e.Message}";
                    return;
                }
                finally { view.ClearPassword(); }
            }
            dlg.Close();
            Toasts.Show(isExisting ? "Changes saved" : $"{saved.Name} added{(password.Length > 0 ? " with saved password" : "")}");
        }, isDefault: true);
        dlg.InitialFocus = view.HostBox;
        dlg.ShowDialog();
    }

    public async Task DeleteAsync(ConnectionItem? item)
    {
        if (item is null || item.IsCentral) return;
        var ok = DialogWindow.Confirm($"Remove {item.Name}?",
            "The system is removed from your list. This does not affect the remote computer. A saved password stays in Windows Credential Manager until you remove it.",
            "Remove system", danger: true);
        if (!ok) return;
        Run(() => controller.Delete(item.Id));
        await Task.Yield();
        vm.ClosePageTab(item.Id);
        Toasts.Show($"{item.Name} removed");
    }

    public void ToggleFavorite(ConnectionItem? item)
    {
        if (item is null) return;
        Run(() =>
        {
            var on = controller.ToggleFavorite(item.Id);
            Toasts.Show(on ? $"{item.Name} added to favorites" : $"{item.Name} removed from favorites");
        });
    }

    public void LoadSamples()
    {
        Run(() =>
        {
            var n = controller.LoadSamples();
            Toasts.Show(n > 0 ? $"{n} sample systems added (mock data)" : "Sample systems are already in your list");
        });
    }

    public void Export(ConnectionItem? item)
    {
        if (item is null) return;
        var name = string.Concat(item.Name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or ' ' or '_' ? ch : '_'));
        var dlg = new SaveFileDialog { Title = "Export Remote Desktop file", FileName = $"{name}.rdp", Filter = "Remote Desktop files (*.rdp)|*.rdp", AddExtension = true };
        if (dlg.ShowDialog(DialogWindow.ActiveOwner()) != true) return;
        Run(() => { controller.ExportRdp(item.Id, dlg.FileName); Toasts.Show("Remote Desktop file exported"); }, "The file could not be exported");
    }

    public async Task ImportAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import connections",
            Filter = "Connection files (.rdp, .rdg, confCons.xml)|*.rdp;*.rdg;*.xml|Remote Desktop files|*.rdp|Remote Desktop Connection Manager|*.rdg|mRemoteNG|*.xml",
            Multiselect = true,
        };
        if (dlg.ShowDialog(DialogWindow.ActiveOwner()) != true) return;
        var files = dlg.FileNames;
        List<ImportPreviewItem> items;
        try { items = await Task.Run(() => controller.ReadImportFiles(files)); }
        catch (InvalidOperationException e) { Error("Files could not be imported", e.Message); return; }
        if (items.Count == 0) return;
        var valid = items.Where(i => i.Connection is { Host.Length: > 0 }).ToList();
        const int previewMax = 60;
        var rows = new List<ImportPreviewRow>();
        var shown = 0;
        foreach (var i in items)
        {
            if (i.Notice is not null) { rows.Add(new ImportPreviewRow("notice", i.File, i.Notice, "", [])); continue; }
            if (i.Error is not null || i.Connection is null || i.Connection.Host.Length == 0)
            {
                rows.Add(new ImportPreviewRow("error", $"{i.File} cannot be imported", i.Error ?? "The file does not contain a computer address.", "", []));
                continue;
            }
            if (++shown > previewMax) continue;
            var c = i.Connection;
            var r = c.Redirect ?? new RedirectOptions();
            var perms = new[] { r.Clipboard ? "Clipboard" : null, r.Printers ? "Printers" : null, r.Microphone ? "Microphone" : null, r.Smartcards ? "Smart cards" : null }.Where(p => p is not null);
            var detail = $"{c.Host}{(c.Port is 0 or 3389 ? "" : $":{c.Port}")}{(c.Gateway is { Mode: not "none" } g ? $" via gateway {g.Host}" : "")}";
            rows.Add(new ImportPreviewRow("ok", string.IsNullOrEmpty(c.Name) ? c.Host : c.Name, detail,
                $"Permissions: {(perms.Any() ? string.Join(", ", perms) : "none")}{(c.Username.Length > 0 ? $" · User: {c.Username}" : "")}", i.Warnings));
        }
        var more = valid.Count > previewMax ? $"… and {valid.Count - previewMax} more {(valid.Count - previewMax == 1 ? "connection" : "connections")}." : null;
        var view = new ImportPreviewView(rows, more);
        var d = new DialogWindow($"Import {valid.Count} {(valid.Count == 1 ? "connection" : "connections")}",
            "Check the target systems and requested permissions before you import. Files from email or downloads can be used for phishing.", view, 760);
        d.AddButton("Cancel", "Btn.Secondary", d.Close, isCancel: true);
        var import = d.AddButton($"Import {valid.Count} {(valid.Count == 1 ? "system" : "systems")}", "Btn.Primary", () =>
        {
            try
            {
                var n = controller.ConfirmImport(valid.Select(v => v.Connection!));
                d.Close();
                Toasts.Show($"{n} {(n == 1 ? "system" : "systems")} imported");
            }
            catch (Exception e) when (e is ValidationException or InvalidOperationException) { Error("Systems could not be imported", e.Message); }
        }, isDefault: true);
        import.IsEnabled = valid.Count > 0;
        d.ShowDialog();
    }

    // ── Menus ────────────────────────────────────────────
    public void ShowSystemMenu(ConnectionItem? c)
    {
        if (c is null) return;
        var menu = new ContextMenu();
        void Add(string label, string icon, Action action, bool enabled = true, bool danger = false)
        {
            var item = new MenuItem
            {
                Header = label,
                IsEnabled = enabled,
                Icon = new Controls.Icon { Data = (System.Windows.Media.Geometry)Application.Current.Resources["Icon." + icon], Size = 16 },
            };
            if (danger) item.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["ErrorBrush"];
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("View details", "info", () => vm.OpenDetails(c.Id));
        if (c.IsConnected) Add("Show session window", "window", () => FocusSession(c.ActiveSessionId));
        else Add(c.Protocol == Protocols.Web ? "Open in browser" : "Connect", c.Protocol == Protocols.Web ? "external" : "connect", () => _ = ConnectAsync(c));
        if (c.IsRdp && !c.IsConnected) Add("Connect with options…", "settings", () => _ = ConnectWithOptionsAsync(c));
        Add("Edit", "edit", () => Edit(c), !c.IsCentral);
        Add("Duplicate", "copy", () => Duplicate(c));
        if (c.IsRdp) Add("Export .rdp file", "download", () => Export(c));
        menu.Items.Add(new Separator());
        Add("Remove system", "trash", () => _ = DeleteAsync(c), !c.IsCentral, danger: true);
        Open(menu);
    }

    private static void Open(ContextMenu menu)
    {
        // Opened from a button (mouse or keyboard): below the button; otherwise at the mouse pointer.
        if (Keyboard.FocusedElement is ButtonBase target)
        {
            menu.PlacementTarget = target;
            menu.Placement = PlacementMode.Bottom;
        }
        else
        {
            menu.Placement = PlacementMode.MousePoint;
        }
        menu.IsOpen = true;
    }

    public void ShowUserMenu(UIElement? anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = $"{vm.User}\n{vm.Computer}", IsEnabled = false });
        if (vm.AuthConfigured)
        {
            menu.Items.Add(new Separator());
            var auth = vm.Auth;
            if (auth.SignedIn)
            {
                menu.Items.Add(new MenuItem { Header = $"Signed in as {auth.Username}", IsEnabled = false });
                var signOut = new MenuItem { Header = "Sign out of Microsoft Entra ID" };
                signOut.Click += async (_, _) => await controller.Auth.SignOutAsync();
                menu.Items.Add(signOut);
            }
            else
            {
                var signIn = new MenuItem { Header = "Sign in with Microsoft…" };
                signIn.Click += async (_, _) => await SignInAsync();
                menu.Items.Add(signIn);
            }
        }
        menu.Items.Add(new Separator());
        void Add(string label, Action a) { var i = new MenuItem { Header = label }; i.Click += (_, _) => a(); menu.Items.Add(i); }
        Add("Settings", () => vm.Navigate("settings"));
        Add("Open data folder", OpenDataFolder);
        Add("Help and troubleshooting", () => vm.Navigate("help"));
        menu.IsOpen = true;
    }

    public void ShowNotifications()
    {
        var view = new NotificationsView(vm);
        var dlg = new DialogWindow("Notifications", "Events from this app session", view, 520);
        dlg.AddButton("Close", "Btn.Secondary", dlg.Close, isDefault: true, isCancel: true);
        dlg.ShowDialog();
    }

    public void SaveViewPrefs()
    {
        var s = Settings;
        s.View = vm.View;
        s.FiltersOpen = vm.FiltersOpen;
        s.SidebarCollapsed = vm.SidebarCollapsed;
        try { controller.Store.SaveSettings(s); } catch (ValidationException) { }
    }

    public void SettingsSaved()
    {
        if (Application.Current is App app) app.ScheduleStatus(controller);
        vm.QueueRebuild();
    }

    // ── Data and help ────────────────────────────────────
    public void OpenDataFolder() => OpenFolder(controller.Paths.DataDir);

    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { Error("The folder could not be opened", e.Message); }
    }

    /// <summary>Only https links to the help site configured by IT are opened.</summary>
    public void OpenHelpUrl()
    {
        if (controller.Policy.HelpUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)
            Infrastructure.Launchers.Launchers.OpenUrl(u.AbsoluteUri);
    }

    public void StartEmpty()
    {
        controller.Store.AcknowledgeCorrupt();
        vm.ResetLoadError();
        vm.Reload();
        Toasts.Show("Started with an empty list");
    }

    public async Task RefreshCentralAsync()
    {
        await controller.RefreshCentralAsync();
        vm.RefreshBanners();
        Toasts.Show("IT system list refreshed");
    }

    public void ReportMigration(MigrationRecord? record)
    {
        if (record is null) return;
        if (record.Status == ElectronDataMigrator.Failed)
            vm.Notify("error", "Data from the previous app version could not be imported", record.Error ?? "");
        else if (record.Imported > 0 && DateTimeOffset.TryParse(record.CompletedAt, out var t) && DateTimeOffset.UtcNow - t < TimeSpan.FromMinutes(2))
        {
            vm.Notify("success", "Your systems were taken over from the previous app version", $"{record.Imported} systems imported. A backup is kept in {record.Backup}.");
            Toasts.Show($"{record.Imported} {(record.Imported == 1 ? "system" : "systems")} taken over from the previous app version", "info", 8000);
        }
        if (record.Rejected > 0) vm.Notify("error", $"{record.Rejected} systems from the previous app version could not be imported", $"Details: {controller.Paths.MigrationRejectedFile}");
    }

    public async Task RerunMigrationAsync()
    {
        // Pending writes first, so they cannot overwrite the merged file afterwards.
        await controller.Store.FlushAsync();
        var record = await Task.Run(() => new ElectronDataMigrator(controller.Paths).Run());
        controller.Store.Load();
        vm.Reload();
        if (record.Status == ElectronDataMigrator.Completed) Toasts.Show($"Import finished: {record.Imported} new, {record.Merged} already present, {record.Rejected} not imported");
        else Error("The import did not work", record.Error ?? "Unknown error.");
        if (vm.CurrentPage is SettingsPageViewModel s) s.ReloadMigration();
    }

    public async Task SignInAsync()
    {
        var state = await controller.Auth.SignInInteractiveAsync();
        if (state.SignedIn) Toasts.Show($"Signed in as {state.Username}");
        else if (state.Error is not null) Error("Sign-in did not work", state.Error);
    }

    // ── Credentials ──────────────────────────────────────
    public void SaveCredential(ConnectionItem item, Action onDone)
    {
        var form = new CredentialForm(item.Model.Username, showInfo: true, item.Host);
        var dlg = new DialogWindow("Save password", $"For {item.Host}", form, 480);
        dlg.AddButton("Cancel", "Btn.Secondary", dlg.Close, isCancel: true);
        dlg.AddButton("Save password", "Btn.Primary", () =>
        {
            if (form.Username.Length == 0 || form.Password.Length == 0) { form.Error = "Enter both username and password."; return; }
            try
            {
                controller.SaveCredential(item.Id, form.Username, form.Password);
                form.Clear();
                dlg.Close();
                Toasts.Show("Password saved in Windows Credential Manager");
                onDone();
            }
            catch (InvalidOperationException e) { form.Error = $"The password could not be saved. {e.Message}"; }
        }, isDefault: true);
        dlg.InitialFocus = form.InitialFocus;
        dlg.ShowDialog();
    }

    public async Task RemoveCredentialAsync(ConnectionItem item, Action onDone)
    {
        var ok = DialogWindow.Confirm("Remove saved password?",
            $"The saved password for {item.Host} is deleted from Windows Credential Manager. Windows asks for your password at the next connection.", "Remove password", danger: true);
        if (!ok) return;
        await Task.Yield();
        Run(() => { controller.DeleteCredential(item.Id); Toasts.Show("Saved password removed"); onDone(); }, "Password could not be removed");
    }

    // ── Sessions ─────────────────────────────────────────
    public void FocusSession(string? sessionId)
    {
        if (sessionId is null) return;
        Run(() => controller.FocusSession(sessionId), "The session window could not be shown");
    }

    public async Task DisconnectAsync(string? sessionId)
    {
        var s = vm.Sessions.FirstOrDefault(x => x.Id == sessionId);
        if (s is null) return;
        if (Settings.ConfirmDisconnect)
        {
            var ok = DialogWindow.Confirm($"Disconnect from {s.Name}?",
                "The Remote Desktop window closes. Your session and open programs keep running on the server, and you can reconnect later.",
                "Disconnect", danger: true, detail: "To end the session and close your programs, select Sign out inside the remote session instead.");
            if (!ok) return;
        }
        await Task.Yield();
        controller.Disconnect(s.Id);
        Toasts.Show($"Disconnected from {s.Name}");
    }

    public async Task ReconnectAsync(string? connectionId)
    {
        var item = connectionId is null ? null : vm.ItemById(connectionId);
        if (item is null) { Error("System not found", "This system was removed from your list."); return; }
        await RunConnectAsync(item.Model, null);
    }

    /// <summary>Started outside the window (jump list, tray, second start): connect like a click on Connect.</summary>
    public void HandleConnectRequest(string id)
    {
        if (!vm.CanQuickConnect) return; // access denied, sign-in missing or data not loaded
        var c = vm.ItemById(id);
        if (c is null) { Error("System not found", "The system is no longer in your list."); return; }
        if (c.IsConnected) { FocusSession(c.ActiveSessionId); return; }
        if (Application.Current.Windows.OfType<DialogWindow>().Any()) { Toasts.Show($"Finish the open dialog, then connect to {c.Name}.", "info"); return; }
        _ = ConnectAsync(c);
    }

    /// <summary>Connect: starts right away with the saved options. The first connection offers to save a password.</summary>
    public async Task ConnectAsync(ConnectionItem? item)
    {
        if (item is null) return;
        if (item.IsConnected) { AlreadyConnected(item.Model, item.ActiveSessionId!); return; }
        var conn = item.Model;
        if (Protocols.Of(conn) == Protocols.Rdp && await ShouldOfferPasswordAsync(conn))
        {
            var next = OfferPassword(conn);
            if (next is null) return;
            conn = next;
        }
        await RunConnectAsync(conn, null);
    }

    public async Task ConnectWithOptionsAsync(ConnectionItem? item)
    {
        if (item is null) return;
        if (item.IsConnected) { AlreadyConnected(item.Model, item.ActiveSessionId!); return; }
        if (!item.IsRdp) { await RunConnectAsync(item.Model, null); return; }
        var model = new ConnectOptionsViewModel(item.Model, controller.Policy, TabsMode);
        _ = Task.Run(() => controller.GetCredential(item.Id)).ContinueWith(t => model.CredentialText = t.IsCompletedSuccessfully && t.Result.Saved
            ? $"A password for {t.Result.Username} is saved in Windows Credential Manager and will be used."
            : $"Windows will ask for your password when the connection starts.{(controller.Policy.AllowSavedCredentials != false ? " You can save it in the system details." : "")}",
            TaskScheduler.FromCurrentSynchronizationContext());
        var view = new ConnectOptionsView(model);
        var dlg = new DialogWindow(model.Title, model.Address, view, 620);
        ConnectOverrides? overrides = null;
        dlg.AddButton("Cancel", "Btn.Secondary", dlg.Close, isCancel: true);
        dlg.AddButton("Connect", "Btn.Primary", () =>
        {
            if (!model.Options.Validate()) { view.ShowAdvanced(); return; }
            var (display, redirect, gateway, security) = model.Options.Read();
            overrides = new ConnectOverrides { Display = display, Redirect = redirect, Gateway = gateway, Security = security, Username = model.Username.Trim(), PromptAlways = model.PromptAlways };
            if (model.Remember)
            {
                try
                {
                    var c = item.Model.Clone();
                    c.Display = display; c.Redirect = redirect; c.Gateway = gateway; c.Security = security; c.Username = model.Username.Trim();
                    controller.Save(c);
                }
                catch (Exception e) when (e is ValidationException or InvalidOperationException) { dlg.ShowNote("error", "The system could not be saved", e.Message); overrides = null; return; }
            }
            dlg.Close();
        }, isDefault: true);
        dlg.ShowDialog();
        if (overrides is not null) await RunConnectAsync(item.Model, overrides);
    }

    private async Task<bool> ShouldOfferPasswordAsync(Connection conn)
    {
        if (conn.Adhoc || conn.LastConnectedAt is not null || controller.Policy.AllowSavedCredentials == false) return false;
        try { return !(await Task.Run(() => controller.GetCredential(conn.Id))).Saved; }
        catch (InvalidOperationException) { return false; } // Credential Manager not available: just connect
    }

    /// <summary>
    /// Asks once whether to save a password. The button says "No thanks" until a password is typed, then "OK".
    /// Returns the connection to start (the username may have been filled in), or null when closed.
    /// </summary>
    private Connection? OfferPassword(Connection conn)
    {
        var form = new CredentialForm(conn.Username, showInfo: false, conn.Host,
            "Then Windows signs you in automatically next time. The password is stored only in Windows Credential Manager on this computer.");
        var dlg = new DialogWindow($"Save a password for {conn.Name}?", null, form, 460);
        Connection? result = null;
        Button? button = null;
        button = dlg.AddButton("No thanks", "Btn.Primary", () =>
        {
            if (form.Password.Length == 0) { result = conn; dlg.Close(); return; }
            if (form.Username.Length == 0) { form.Error = "Enter the username for this password."; return; }
            try
            {
                controller.SaveCredential(conn.Id, form.Username, form.Password);
                form.Clear();
                var next = conn;
                // Remember the username on the system too, so Windows suggests the right account.
                if (conn.Username.Length == 0 && !conn.IsCentral)
                {
                    var c = conn.Clone();
                    c.Username = form.Username;
                    try { next = controller.Save(c); } catch (Exception e) when (e is ValidationException or InvalidOperationException) { next = c; }
                }
                Toasts.Show("Password saved in Windows Credential Manager");
                result = next;
                dlg.Close();
            }
            catch (InvalidOperationException e) { form.Error = e.Message; }
        }, isDefault: true);
        form.PasswordChanged += () => button.Content = form.Password.Length > 0 ? "OK" : "No thanks";
        dlg.InitialFocus = form.InitialFocus;
        dlg.ShowDialog();
        return result;
    }

    private void AlreadyConnected(Connection conn, string sessionId)
    {
        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"You already have a session to {conn.Name}. Opening a second connection would take over the same remote session." };
        var dlg = new DialogWindow("Already connected", null, body, 460);
        dlg.AddButton("Cancel", "Btn.Secondary", dlg.Close, isCancel: true);
        dlg.AddButton("Show session window", "Btn.Primary", () => { dlg.Close(); FocusSession(sessionId); }, isDefault: true, icon: "window");
        dlg.ShowDialog();
    }

    /// <summary>Quick connect (Ctrl+K): type an address and connect without creating a system first.</summary>
    public void QuickConnect(string prefill)
    {
        if (!vm.CanQuickConnect || Application.Current.Windows.OfType<DialogWindow>().Any()) return;
        var model = new QuickConnectViewModel(vm.Items, controller.ProtocolAllowed, prefill);
        var view = new QuickConnectView(model);
        var dlg = new DialogWindow("Quick connect", model.Subtitle, view, 640);
        QuickOption? chosen = null;
        view.Chosen += o => { chosen = o; dlg.Close(); };
        dlg.InitialFocus = view.Input;
        dlg.ShowDialog();
        if (chosen is null) return;
        if (chosen.System is { } sys)
        {
            if (sys.IsConnected) FocusSession(sys.ActiveSessionId);
            else _ = RunConnectAsync(sys.Model, null, quick: true);
            return;
        }
        try
        {
            var conn = controller.QuickTarget(model.Text, chosen.Target!.Protocol, model.Save);
            if (model.Save) Toasts.Show($"{conn.Name} saved to My systems");
            var active = controller.Sessions.ActiveFor(conn.Id);
            if (active is not null) { FocusSession(active.Id); return; }
            _ = RunConnectAsync(conn, null, quick: true);
        }
        catch (Exception e) when (e is InvalidOperationException or ValidationException)
        {
            Error("Quick connect is not possible", e.Message);
        }
    }

    // ── Connection progress ──────────────────────────────
    public async Task RunConnectAsync(Connection conn, ConnectOverrides? overrides, bool force = false, bool quick = false)
    {
        var protocol = Protocols.Of(conn);
        if (protocol == Protocols.Web)
        {
            try
            {
                if (await controller.ConnectAsync(conn.Id, overrides, force, quick) is OpenedOutcome) Toasts.Show($"{conn.Name} opened in your browser");
            }
            catch (Exception e) when (e is InvalidOperationException or ValidationException) { Error($"{conn.Name} could not be opened", e.Message); }
            return;
        }
        var model = new ConnectProgressViewModel(conn);
        var view = new ConnectProgressView(model);
        var dlg = new DialogWindow($"Connecting to {conn.Name}", conn.Host, view, 520);
        var finished = false;
        string? sessionId = null;
        string? shownHint = null;
        DispatcherTimer? slow = null;
        var d = Dispatcher.CurrentDispatcher;

        void OnProgress(ConnectStep s) => d.BeginInvoke(() => { if (!finished && s.ConnectionId == conn.Id) model.SetStep(s.Step, s.State); });
        void OnSession(SessionInfo s) => d.BeginInvoke(() => HandleSession(s));
        controller.ConnectProgress += OnProgress;
        controller.Sessions.Updated += OnSession;
        dlg.Closed += (_, _) =>
        {
            finished = true;
            slow?.Stop();
            controller.ConnectProgress -= OnProgress;
            controller.Sessions.Updated -= OnSession;
        };
        dlg.AddButton("Run in background", "Btn.Secondary", dlg.Close, isCancel: true);

        void ShowResult(SessionResult? r, string host, DateTimeOffset started)
        {
            r ??= new SessionResult("error", "Connection could not be established", "");
            var technical = r.Code is null ? null : $"Code: {r.Code}\nHost: {host}\nStarted: {started.ToLocalTime():g}";
            model.Note(r.Kind == "normal" ? "info" : "error", r.Title, r.Message + (r.Retry ? " Trying again is safe." : ""), technical);
            dlg.ClearButtons();
            dlg.AddButton("Close", "Btn.Secondary", dlg.Close, isCancel: true);
            var retry = dlg.AddButton("Try again", "Btn.Primary", () => { dlg.Close(); _ = RunConnectAsync(conn, overrides, quick: quick); }, isDefault: true, icon: "refresh");
            retry.Focus();
        }

        void HandleSession(SessionInfo s)
        {
            if (finished || s.Id != sessionId) return;
            if (s.State == SessionState.Connecting && s.Result is { Hint: true } hint && shownHint != hint.Code)
            {
                // For example a wrong password: Remote Desktop lets the user try again in its own window.
                shownHint = hint.Code;
                model.Extra = $"{hint.Title}. {hint.Message} You can try again in the Remote Desktop window.";
                return;
            }
            if (s.State == SessionState.Active)
            {
                model.SetStep("session", "done");
                Toasts.Show(protocol == Protocols.Ssh ? $"Terminal for {conn.Name} opened" : $"Connected to {conn.Name}");
                dlg.Close();
            }
            else if (s.State is SessionState.Failed or SessionState.Ended)
            {
                model.SetStep("session", s.State == SessionState.Failed ? "failed" : "skipped");
                ShowResult(s.Result, s.Host, s.StartedAt);
            }
        }

        dlg.Show();
        ConnectOutcome outcome;
        try
        {
            outcome = await controller.ConnectAsync(conn.Id, overrides, force, quick);
        }
        catch (Exception e) when (e is InvalidOperationException or ValidationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            model.SetStep("start", "failed");
            ShowResult(new SessionResult("error", "Connection could not be established", e.Message), conn.Host, DateTimeOffset.UtcNow);
            return;
        }
        if (finished) return;
        switch (outcome)
        {
            case AlreadyActiveOutcome a:
                dlg.Close();
                AlreadyConnected(conn, a.Session.Id);
                return;
            case InProgressOutcome:
                dlg.Close();
                Toasts.Show($"A connection to {conn.Name} is already being started", "info");
                return;
            case UnreachableOutcome u:
                model.Note("error", u.Title, u.Message + $" Trying again is safe. \"Connect anyway\" starts the connection without the availability check, for example when only {(protocol == Protocols.Ssh ? "a jump host" : "the gateway")} can reach the system.", u.Technical);
                dlg.ClearButtons();
                dlg.AddButton("Close", "Btn.Secondary", dlg.Close, isCancel: true);
                dlg.AddButton("Connect anyway", "Btn.Secondary", () => { dlg.Close(); _ = RunConnectAsync(conn, overrides, force: true, quick: quick); });
                dlg.AddButton("Try again", "Btn.Primary", () => { dlg.Close(); _ = RunConnectAsync(conn, overrides, quick: quick); }, isDefault: true, icon: "refresh").Focus();
                return;
            case StartedOutcome { Session.LaunchMode: "embedded" }:
                // Started inside the app: the tab shows the progress itself.
                dlg.Close();
                return;
            case StartedOutcome st:
                sessionId = st.Session.Id;
                if (protocol == Protocols.Ssh)
                {
                    HandleSession(controller.Sessions.Get(sessionId) ?? st.Session);
                    return;
                }
                model.SetStep("session", "running");
                var launchNote = st.Session.LaunchMode == "file" && !st.Session.Signed
                    ? "Windows shows a security confirmation for the connection. Check the computer name and the listed permissions, then select Connect."
                    : "The Remote Desktop window opens separately.";
                model.Note("info", "Continue in the Remote Desktop window",
                    $"{launchNote} {(st.CredentialSaved ? "Your saved password is used." : "Windows asks for your password.")}{(st.LaunchNote is null ? "" : " " + st.LaunchNote)}");
                dlg.ClearButtons();
                dlg.AddButton("Run in background", "Btn.Secondary", dlg.Close, isCancel: true);
                dlg.AddButton("Cancel connection", "Btn.Danger", () => { controller.Disconnect(sessionId); dlg.Close(); });
                // The session may already have connected or failed while the connect call was returning.
                if (controller.Sessions.Get(sessionId) is { State: not SessionState.Connecting } newer) HandleSession(newer);
                slow = new DispatcherTimer(TimeSpan.FromSeconds(25), DispatcherPriority.Background, (_, _) =>
                {
                    slow!.Stop();
                    if (!finished) model.Extra ??= "This is taking longer than usual. Remote Desktop may be waiting for your confirmation or password in its own window. You can keep this running in the background.";
                }, d);
                slow.Start();
                return;
        }
    }
}

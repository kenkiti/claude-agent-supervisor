using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using AgentSupervisor.App.Tray;
using AgentSupervisor.Core;
using AgentSupervisor.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.FileProviders;

var layout = new LocalAppDataLayout();
layout.EnsureDirectories();
var command = args.FirstOrDefault()?.ToLowerInvariant();
var wslDistro = Environment.GetEnvironmentVariable("AGENTSUPERVISOR_WSL_DISTRO") ?? "Ubuntu";
if (command is "hook-forward") { await HookForwardAsync(args.Skip(1).ToArray()); return; }
if (command is "install" or "uninstall" or "restore-db") { await RunLifecycleCommandAsync(command, args.Skip(1).ToArray()); return; }
if (command is "update") { await RunUpdateCommandAsync(args.Skip(1).ToArray()); return; }
if (command is null && Environment.GetEnvironmentVariable("AGENTSUPERVISOR_SKIP_FIRST_RUN") != "1" && Environment.GetEnvironmentVariable("AGENTSUPERVISOR_SKIP_SINGLE_INSTANCE") != "1") await RunFirstRunIfNeededAsync(layout);
// WebApplicationFactory<Program>-based integration tests intercept this entry point and
// invoke it once per test-fixture host build. A real system-wide "Global\" mutex would
// make every test class after the first one see owner=false and return here before ever
// calling WebApplication.CreateBuilder -- deterministically, not just under a race, since
// the first test fixture's host stays alive (and keeps the mutex) for the rest of the run.
var skipSingleInstance = Environment.GetEnvironmentVariable("AGENTSUPERVISOR_SKIP_SINGLE_INSTANCE") == "1";
Mutex? mutex = null;
if (!skipSingleInstance)
{
    mutex = new Mutex(true, "Global\\AgentSupervisor.SingleInstance", out var owner);
    if (!owner) return;
}
using var mutexScope = mutex;
var builder = WebApplication.CreateBuilder(args);
// DESIGN.md 4.3: default to port 8700, automatically pick a free port if it is already in use.
var port = IsPortAvailable(8700) ? 8700 : 0;
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

static bool IsPortAvailable(int port)
{
    try
    {
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
        return true;
    }
    catch (SocketException)
    {
        return false;
    }
}
builder.Services.AddRazorPages();
builder.Services.AddSingleton(layout);
builder.Services.AddSingleton<SqliteMigrationRunner>();
builder.Services.AddSingleton<FileBackupService>();
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddSingleton<LogRotationService>();
builder.Services.AddSingleton<FileLogWriter>(s => new FileLogWriter(layout.Logs));
builder.Services.AddSingleton<DiagnosticsExportService>();
builder.Services.AddSingleton<ITaskScheduler, WindowsTaskScheduler>();
builder.Services.AddHostedService<LogRotationWorker>();
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<IClaudeRuntime, WindowsClaudeRuntime>();
var wslClaudeCommand = ResolveWslClaudeCommand(wslDistro);
builder.Services.AddSingleton<IClaudeRuntime>(s => new WslClaudeRuntime(s.GetRequiredService<IProcessRunner>(), wslDistro, wslClaudeCommand));
builder.Services.AddSingleton<SessionSnapshotStore>(s => new SessionSnapshotStore($"Data Source={Path.Combine(layout.Data, "agentsupervisor.db")}"));
builder.Services.AddSingleton<ProjectRegistryStore>(s => new ProjectRegistryStore($"Data Source={Path.Combine(layout.Data, "agentsupervisor.db")}"));
builder.Services.AddSingleton<TaskStore>(s => new TaskStore($"Data Source={Path.Combine(layout.Data, "agentsupervisor.db")}"));
builder.Services.AddSingleton<PendingQuestionStore>(s => new PendingQuestionStore($"Data Source={Path.Combine(layout.Data, "agentsupervisor.db")}"));
builder.Services.AddSingleton<RecoveryHistoryStore>(s => new RecoveryHistoryStore($"Data Source={Path.Combine(layout.Data, "agentsupervisor.db")}"));
builder.Services.AddSingleton<TaskQueue>();
builder.Services.AddHostedService(s => s.GetRequiredService<TaskQueue>());
builder.Services.AddSingleton<HookEventStore>(s => new HookEventStore($"Data Source={Path.Combine(layout.Data, "agentsupervisor.db")}"));
builder.Services.AddSingleton<SecretStore>();
builder.Services.AddSingleton<IHookTokenProvider, HookTokenProvider>();
builder.Services.AddSingleton<WebhookSecretStore>();
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<INotificationChannel>(s => new DiscordWebhookChannel(s.GetRequiredService<HttpClient>(), () => s.GetRequiredService<WebhookSecretStore>().Get("discord")));
builder.Services.AddSingleton<INotificationChannel>(s => new SlackWebhookChannel(s.GetRequiredService<HttpClient>(), () => s.GetRequiredService<WebhookSecretStore>().Get("slack")));
builder.Services.AddSingleton<INotificationChannel, SoundChannel>();
if (WindowsFormsApplicationSupported())
{
    // Single shared NotifyIcon: WindowsBalloonChannel uses it for balloon notifications,
    // and TrayHost (constructed later, once the host is running) attaches the Exit menu
    // to this same instance. Two separate NotifyIcon instances used to render as two
    // indistinguishable tray icons, only one of which could actually be closed.
    builder.Services.AddSingleton(new NotifyIcon { Icon = LoadApplicationIcon(), Visible = true });
    builder.Services.AddSingleton<INotificationChannel, WindowsBalloonChannel>();
}

static System.Drawing.Icon LoadApplicationIcon()
{
    try
    {
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("AgentSupervisor.App.assets.app.ico");
        return stream is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(stream);
    }
    catch
    {
        return System.Drawing.SystemIcons.Application;
    }
}
builder.Services.AddSingleton<AlertEngine>();
builder.Services.AddSingleton<INotificationOutbox>(s => new SqliteNotificationOutbox($"Data Source={Path.Combine(layout.Data, "agentsupervisor.db")}", s.GetServices<INotificationChannel>()));
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<RuntimePoller>();
builder.Services.AddHostedService(s => s.GetRequiredService<RuntimePoller>());
var app = builder.Build();
// DESIGN.md 7.1: CSS/JS ship embedded in the assembly, not as loose files beside the EXE
// (PublishSingleFile does not bundle physical wwwroot files into the single file).
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot"),
    RequestPath = ""
});
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<SqliteMigrationRunner>().Migrate($"Data Source={Path.Combine(layout.Data, "agentsupervisor.db")}", layout.Backups);
// DESIGN.md Phase 3: push updates to the dashboard without waiting for the next poll,
// both from the 10s background poller and from hook events as they arrive.
app.Services.GetRequiredService<RuntimePoller>().SnapshotSaved += runtimeId =>
    app.Services.GetRequiredService<IHubContext<SessionHub>>().Clients.All.SendAsync("sessionsUpdated", runtimeId);
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/v1/diagnostics/export", (HttpRequest request, IHookTokenProvider tokens, DiagnosticsExportService exporter, LocalAppDataLayout l) => { var runtime = request.Query["runtime"].FirstOrDefault() ?? "windows"; if (!request.Headers.Authorization.ToString().Equals("Bearer " + tokens.GetToken(runtime), StringComparison.Ordinal)) return Results.Unauthorized(); return Results.File(exporter.Export(Path.Combine(l.Runtime, "diagnostics.zip"), l.Logs, Path.Combine(l.Data, "agentsupervisor.db"), Path.Combine(l.Config, "settings.json"), typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"), "application/zip", "diagnostics.zip"); });
app.MapGet("/api/v1/sessions", (SessionSnapshotStore store) => Results.Ok(store.ReadLatest()));
app.MapGet("/api/v1/sessions/{id}", (string id, SessionSnapshotStore store) => store.ReadLatest().FirstOrDefault(x => x.Id == id) is { } s ? Results.Ok(s) : Results.NotFound());
app.MapGet("/api/v1/sessions/observe-only", (SessionSnapshotStore store, HookEventStore hookStore) => Results.Ok(hookStore.ReadObserveOnly(store.ReadLatest().Select(x => x.Id).ToHashSet())));
app.MapGet("/api/v1/projects", (ProjectRegistryStore s) => Results.Ok(s.Projects()));
app.MapPost("/api/v1/projects", (ProjectRecord x, ProjectRegistryStore s) => { s.AddProject(x); return Results.Created($"/api/v1/projects/{x.Id}", x); });
app.MapPut("/api/v1/projects/{id}", (string id, ProjectRecord x, ProjectRegistryStore s) => { s.UpdateProject(id, x); return Results.Ok(x); });
app.MapDelete("/api/v1/projects/{id}", (string id, ProjectRegistryStore s) => { s.DeleteProject(id); return Results.NoContent(); });
app.MapGet("/api/v1/runtimes", (ProjectRegistryStore s) => Results.Ok(s.Runtimes()));
app.MapPost("/api/v1/runtimes", (RuntimeRecord x, ProjectRegistryStore s) => { s.AddRuntime(x); return Results.Created($"/api/v1/runtimes/{x.Id}", x); });
app.MapGet("/api/v1/tasks", (TaskStore s) => Results.Ok(s.All()));
app.MapGet("/api/v1/tasks/{id}", (string id, TaskStore s, RecoveryHistoryStore h) => s.Get(id) is { } t ? Results.Ok(new { task = t, attempts = s.Attempts(id), recoveryHistory = h.ForTask(id) }) : Results.NotFound());
app.MapPost("/api/v1/tasks", (TaskSubmission x, TaskStore s, TaskQueue q) => { var t = s.Add(x.ProjectId, x.Prompt, x.Mode, x.MaxTurns, x.MaxBudgetUsd, x.TimeoutSeconds); q.Enqueue(t.Id); return Results.Accepted($"/api/v1/tasks/{t.Id}", t); });
app.MapPost("/api/v1/tasks/{id}/cancel", (string id, TaskStore s) => { s.Status(id, "cancelled"); return Results.Ok(); });
app.MapPost("/api/v1/tasks/{id}/manual-stop", (string id, TaskStore s) => { if (s.Get(id) is null) return Results.NotFound(); s.ManualStop(id); return Results.Ok(new { id, manualStop = true }); });
app.MapGet("/api/v1/questions", (PendingQuestionStore s) => Results.Ok(s.All()));
app.MapGet("/api/v1/questions/{id}", (string id, PendingQuestionStore s) => s.Get(id) is { } q ? Results.Ok(q) : Results.NotFound());
app.MapPost("/api/v1/questions", (QuestionSubmission x, HttpRequest request, PendingQuestionStore s, IHookTokenProvider tokens, AlertEngine alerts, INotificationOutbox outbox) => { if (!request.Headers.Authorization.ToString().Equals("Bearer " + tokens.GetToken(x.RuntimeId), StringComparison.Ordinal)) return Results.Unauthorized(); var q = s.Create(x.RuntimeId, x.SessionId, x.QuestionsJson); var c = alerts.QuestionPending(q); if (c is not null) outbox.Enqueue(c, alerts.GetCooldownSeconds(c.NotificationType)); return Results.Created($"/api/v1/questions/{q.Id}", q); });
app.MapPost("/api/v1/questions/{id}/answer", async (string id, HttpRequest request, PendingQuestionStore s) => { if (s.Get(id) is null) return Results.NotFound(); string answers; if (request.HasFormContentType) { var form = await request.ReadFormAsync(); var question = JsonDocument.Parse(s.Get(id)!.QuestionsJson).RootElement; var map = new Dictionary<string, object?>(); for (var i = 0; i < question.GetArrayLength(); i++) { var text = question[i].GetProperty("question").GetString()!; var other = form["other" + i].FirstOrDefault(); var selected = form["q" + i].ToArray(); map[text] = !string.IsNullOrWhiteSpace(other) ? other : question[i].GetProperty("multiSelect").GetBoolean() ? selected : selected.FirstOrDefault(); } answers = JsonSerializer.Serialize(map); } else { using var body = await JsonDocument.ParseAsync(request.Body); answers = body.RootElement.TryGetProperty("answersJson", out var raw) ? raw.GetString() ?? "{}" : body.RootElement.TryGetProperty("answers", out var answerMap) ? answerMap.GetRawText() : "{}"; } return s.Answer(id, answers) ? Results.Ok(s.Get(id)) : Results.Conflict(); });
app.MapPost("/api/v1/questions/{id}/timeout", (string id, HttpRequest request, PendingQuestionStore s, IHookTokenProvider tokens) => { var runtime = request.Query["runtime"].FirstOrDefault() ?? "windows"; if (!request.Headers.Authorization.ToString().Equals("Bearer " + tokens.GetToken(runtime), StringComparison.Ordinal)) return Results.Unauthorized(); return s.MarkTimedOut(id) ? Results.Ok(s.Get(id)) : Results.NotFound(); });
var hookEventJsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
app.MapPost("/api/v1/hooks/events", async (HttpRequest request, HookEventStore store, IHookTokenProvider tokens, IHubContext<SessionHub> hub, AlertEngine alerts, INotificationOutbox outbox, CancellationToken ct) => { var runtime = request.Query["runtime"].FirstOrDefault() ?? "windows"; if (!request.Headers.Authorization.ToString().Equals("Bearer " + tokens.GetToken(runtime), StringComparison.Ordinal)) return Results.Unauthorized(); if (request.ContentLength > 65536) return Results.StatusCode(413); using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct); var e = JsonSerializer.Deserialize<HookEvent>(doc.RootElement.GetRawText(), hookEventJsonOptions); if (e is null || !HookSecurity.IsAllowed(e.EventName)) return Results.BadRequest(); var p = HookSecurity.Redact(e.Payload); store.Save(e, "hook", p, HookEventStore.Hash(p)); var candidate = alerts.FromHook(e, runtime); if (candidate is not null) outbox.Enqueue(candidate, alerts.GetCooldownSeconds(candidate.NotificationType)); await hub.Clients.All.SendAsync("sessionsUpdated", runtime, cancellationToken: ct); return Results.Ok(); });
app.MapHub<SessionHub>("/hubs/sessions");
app.MapPost("/api/v1/notifications/test/{channel}", async (string channel, IServiceProvider services, CancellationToken ct) =>
{
    var channels = services.GetServices<INotificationChannel>();
    var target = channels.FirstOrDefault(x => x.Id.Equals(channel, StringComparison.OrdinalIgnoreCase));
    if (target is null) return Results.NotFound();
    await target.SendTestAsync(ct);
    return Results.Ok(new { channel = target.Id });
});
app.MapGet("/api/v1/notifications/channels", (WebhookSecretStore secrets) => Results.Ok(new[] { "discord", "slack" }.Select(id => new { id, configured = secrets.Get(id) is not null, maskedSuffix = secrets.GetMaskedSuffix(id) })));
app.MapPost("/api/v1/notifications/channels/{channel}", async (string channel, HttpRequest request, WebhookSecretStore secrets) =>
{
    if (channel is not ("discord" or "slack")) return Results.NotFound();
    using var reader = new StreamReader(request.Body);
    var url = (await reader.ReadToEndAsync()).Trim();
    if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _)) return Results.BadRequest();
    secrets.Set(channel, url);
    // Never echo the URL back -- confirm by id + masked suffix only, matching GET above.
    return Results.Ok(new { channel, configured = true, maskedSuffix = secrets.GetMaskedSuffix(channel) });
});
app.MapRazorPages();

if (WindowsFormsApplicationSupported())
{
    await app.StartAsync();
    using var tray = new TrayHost(app.Services.GetRequiredService<NotifyIcon>(), onExit: () =>
    {
        _ = app.StopAsync();
        Application.Exit();
    });
    Application.Run();
    await app.StopAsync();
}
else
{
    // Test hosts (e.g. WebApplicationFactory<Program>) intercept Build()/Run() before
    // reaching this point and never execute this branch; this path only matters when
    // Windows Forms cannot pump a message loop (non-interactive/service contexts).
    await app.RunAsync();
}

static string ResolveWslClaudeCommand(string distro)
{
    try
    {
        var result = new ProcessRunner().RunAsync("wsl.exe", new[] { "-d", distro, "--", "printenv", "HOME" }).GetAwaiter().GetResult();
        if (result.Succeeded)
        {
            var home = result.Output.Trim();
            if (!string.IsNullOrWhiteSpace(home) && !home.Contains(" "))
                return home.TrimEnd('/') + "/.local/bin/claude";
        }
    }
    catch { }
    return "claude";
}

static bool WindowsFormsApplicationSupported() => Environment.UserInteractive && OperatingSystem.IsWindows();
static async Task RunLifecycleCommandAsync(string command, string[] args)
{
    var wslDistro = Environment.GetEnvironmentVariable("AGENTSUPERVISOR_WSL_DISTRO") ?? "Ubuntu";
    var l = new LocalAppDataLayout(); l.EnsureDirectories(); var runner = new ProcessRunner(); var scheduler = new WindowsTaskScheduler(runner); var exe = Process.GetCurrentProcess().MainModule?.FileName ?? "AgentSupervisor.App.exe"; var dry = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
    if (command == "restore-db") { if (args.FirstOrDefault() is { } backup && File.Exists(backup)) new FileBackupService().Restore(backup, Path.Combine(l.Data, "agentsupervisor.db")); return; }
    var wsl = new WslHookInstaller(runner);
    if (command == "install")
    {
        foreach (var path in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json") })
            new ClaudeSettingsHookMerger(path, "windows", exe).Merge("http://127.0.0.1:8700/api/v1/hooks/events", null, dry);
        await wsl.InstallAsync(exe, wslDistro, dry); if (!await scheduler.RegisterAsync(exe, dry)) Console.Error.WriteLine("Task Scheduler registration failed"); return;
    }
    foreach (var path in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json") }) if (File.Exists(path)) new ClaudeSettingsHookMerger(path, "windows", exe).Uninstall(dry);
    await wsl.UninstallAsync(wslDistro, dry); if (!await scheduler.UnregisterAsync(dry)) Console.Error.WriteLine("Task Scheduler unregistration failed");
}
static async Task RunUpdateCommandAsync(string[] args)
{
    if (args.FirstOrDefault() is not { } staged) { Console.Error.WriteLine("update requires a staged EXE path"); return; }
    var l = new LocalAppDataLayout(); l.EnsureDirectories(); var current = Process.GetCurrentProcess().MainModule?.FileName ?? "AgentSupervisor.App.exe";
    var result = await new UpdateService(new FileBackupService()).StageAndSwapAsync(current, staged, l.Backups, async () => { using var c = new HttpClient(); try { return (await c.GetAsync("http://127.0.0.1:8700/health")).IsSuccessStatusCode; } catch { return false; } }, args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase));
    Console.WriteLine(result ? "update applied" : "update rolled back");
}
static async Task RunFirstRunIfNeededAsync(LocalAppDataLayout layout)
{
    var marker = Path.Combine(layout.Config, "first-run.complete"); if (File.Exists(marker)) return;
    try
    {
        var runner = new ProcessRunner(); var discovery = new RuntimeDiscovery(runner); var scheduler = new WindowsTaskScheduler(runner); var exe = Process.GetCurrentProcess().MainModule?.FileName ?? "AgentSupervisor.App.exe";
        var wslDistro = Environment.GetEnvironmentVariable("AGENTSUPERVISOR_WSL_DISTRO") ?? "Ubuntu";
        var wizard = new AgentSupervisor.App.Bootstrap.FirstRunWizard();
        var result = await wizard.RunAsync(discovery, new[] { ("claude.exe", Array.Empty<string>()), ("wsl.exe", new[] { "-d", wslDistro, "--", "claude", "agents", "--json", "--all" }) }, async () =>
        {
            var win = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json"); if (File.Exists(win)) new ClaudeSettingsHookMerger(win, "windows", exe).Merge("http://127.0.0.1:8700/api/v1/hooks/events", null);
            await new WslHookInstaller(runner).InstallAsync(exe);
        }, scheduler, exe);
        if (result.Runtimes.Any(x => x.Succeeded) && result.HooksInstalled && result.AutoStartEnabled) File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
    }
    catch (Exception ex) { try { new FileLogWriter(layout.Logs).Write("ERROR", "First-run wizard failed", ex); } catch { } }
}
static async Task HookForwardAsync(string[] args) { try { var runtime = args.SkipWhile(x => !x.Equals("--runtime", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault() ?? "windows"; using var reader = new StreamReader(Console.OpenStandardInput()); var rawText = await reader.ReadToEndAsync(); using var doc = JsonDocument.Parse(rawText); var raw = doc.RootElement; var eventName = raw.TryGetProperty("hook_event_name", out var en) ? en.GetString() : null; var tool = raw.TryGetProperty("tool_name", out var tn) ? tn.GetString() : null; if (eventName == "PreToolUse" && tool == "AskUserQuestion") { Console.Write(JsonSerializer.Serialize(new { hookSpecificOutput = new { hookEventName = "PreToolUse", permissionDecision = "allow" } })); return; } var layout = new LocalAppDataLayout(); var token = new HookTokenProvider(layout).GetToken(runtime); using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(5) }; var auth = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token); if (eventName == "PermissionRequest" && tool == "AskUserQuestion") { var questions = raw.GetProperty("tool_input").GetProperty("questions").GetRawText(); var create = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:8700/api/v1/questions") { Content = new StringContent(JsonSerializer.Serialize(new QuestionSubmission(runtime, raw.GetProperty("session_id").GetString(), questions)), System.Text.Encoding.UTF8, "application/json") }; create.Headers.Authorization = auth; var response = await c.SendAsync(create); if (!response.IsSuccessStatusCode) return; var q = JsonSerializer.Deserialize<PendingQuestionRecord>(await response.Content.ReadAsStringAsync(), hookResponseJsonOptions); if (q is null) return; for (var i = 0; i < 750; i++) { await Task.Delay(TimeSpan.FromSeconds(2)); using var poll = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:8700/api/v1/questions/" + q.Id); poll.Headers.Authorization = auth; try { var pr = await c.SendAsync(poll); if (!pr.IsSuccessStatusCode) continue; var current = JsonSerializer.Deserialize<PendingQuestionRecord>(await pr.Content.ReadAsStringAsync(), hookResponseJsonOptions); if (current?.Status == "answered" && current.AnswersJson is not null) { Console.Write(JsonSerializer.Serialize(new { hookSpecificOutput = new { hookEventName = "PermissionRequest", decision = new { behavior = "allow", updatedInput = new { questions = raw.GetProperty("tool_input").GetProperty("questions"), answers = JsonDocument.Parse(current.AnswersJson).RootElement } } } })); return; } } catch { } } using (var timeout = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:8700/api/v1/questions/" + q.Id + "/timeout?runtime=" + Uri.EscapeDataString(runtime))) { timeout.Headers.Authorization = auth; try { await c.SendAsync(timeout); } catch { } } return; } var e = HookPayloadTranslator.Translate(raw, runtime); using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:8700/api/v1/hooks/events?runtime=" + Uri.EscapeDataString(runtime)) { Content = new StringContent(JsonSerializer.Serialize(e), System.Text.Encoding.UTF8, "application/json") }; request.Headers.Authorization = auth; await c.SendAsync(request); } catch { } }

public partial class Program { internal static readonly JsonSerializerOptions hookResponseJsonOptions = new(JsonSerializerDefaults.Web); }

public sealed record TaskSubmission(string ProjectId, string Prompt, string Mode = "batch-print", int MaxTurns = 10, decimal MaxBudgetUsd = 1, int TimeoutSeconds = 3600);
public sealed record QuestionSubmission(string RuntimeId, string? SessionId, string QuestionsJson);
public sealed record QuestionAnswer(string AnswersJson);

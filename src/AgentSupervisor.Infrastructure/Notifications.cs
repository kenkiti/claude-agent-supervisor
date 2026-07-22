using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgentSupervisor.Core;

namespace AgentSupervisor.Infrastructure;

public interface INotificationChannel
{
    string Id { get; }
    Task SendAsync(AlertCandidate candidate, CancellationToken cancellationToken = default);
    Task SendTestAsync(CancellationToken cancellationToken = default);
}

public static class NotificationText
{
    public static string Label(string notificationType) => notificationType switch
    {
        "permission-wait" => "許可待ち: Claude Codeがツールの実行許可を待っています",
        "input-needed" => "入力待ち: Claude Codeがユーザーの入力を待っています",
        "authentication-failure" => "認証エラー: Claude CLIのログイン状態を確認してください",
        "runtime-unreachable" => "ランタイム接続不可: WSLディストロ名またはclaudeコマンドの設定を確認してください",
        "background-failed" => "バックグラウンドセッションが失敗しました",
        "recovery-manual" => "自動復旧を停止しました（手動対応が必要です）",
        "recovery-exhausted" => "自動リトライが上限に達しました",
        "daily-budget-exceeded" => "日次予算の上限を超過しました",
        "daemon-unreachable" => "Claudeデーモンに接続できません",
        "task-completed" => "タスクが完了しました",
        "question-pending" => "Claude Codeからの質問が回答待ちです",
        "test" => "テスト通知",
        _ => notificationType
    };

    public static string? ExtractMessage(AlertCandidate candidate)
    {
        try
        {
            using var document = JsonDocument.Parse(candidate.PayloadJson);
            var root = document.RootElement;
            string? message = null;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var messageProperty) && messageProperty.ValueKind == JsonValueKind.String)
                message = messageProperty.GetString();
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("last_assistant_message", out var lastAssistantMessageProperty) && lastAssistantMessageProperty.ValueKind == JsonValueKind.String)
                message = lastAssistantMessageProperty.GetString();
            else if (root.ValueKind == JsonValueKind.Array && root.EnumerateArray().FirstOrDefault() is var first && first.ValueKind == JsonValueKind.Object && first.TryGetProperty("question", out var questionProperty) && questionProperty.ValueKind == JsonValueKind.String)
                message = questionProperty.GetString();

            message = message?.Trim();
            if (string.IsNullOrEmpty(message)) return null;
            return message.Length > 400 ? message[..400] + "…" : message;
        }
        catch
        {
            return null;
        }
    }

    public static string Compose(AlertCandidate candidate)
    {
        var label = Label(candidate.NotificationType);
        var ruleId = label == candidate.NotificationType ? "" : " (" + candidate.NotificationType + ")";
        var result = "[" + candidate.Severity + "] " + label + ruleId;
        var message = ExtractMessage(candidate);
        return message is null ? result : result + Environment.NewLine + message;
    }
}

public sealed class DiscordWebhookChannel : INotificationChannel
{
    private readonly HttpClient _http;
    private readonly Func<string?> _url;
    public string Id => "discord";
    public DiscordWebhookChannel(HttpClient http, Func<string?> url) { _http = http; _url = url; }
    public async Task SendAsync(AlertCandidate candidate, CancellationToken cancellationToken = default)
    {
        var url = _url();
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("Discord webhook is not configured.");
        using var response = await _http.PostAsJsonAsync(url, new { content = NotificationText.Compose(candidate) }, cancellationToken);
        if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500) throw new TransientNotificationException(response.StatusCode);
        response.EnsureSuccessStatusCode();
    }
    public Task SendTestAsync(CancellationToken cancellationToken = default) => SendAsync(new AlertCandidate("test", "info", "", null, 0, "{}", new[] { Id }), cancellationToken);
}

public sealed class SlackWebhookChannel : INotificationChannel
{
    private readonly HttpClient _http;
    private readonly Func<string?> _url;
    public string Id => "slack";
    public SlackWebhookChannel(HttpClient http, Func<string?> url) { _http = http; _url = url; }
    public async Task SendAsync(AlertCandidate candidate, CancellationToken cancellationToken = default)
    {
        var url = _url();
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("Slack webhook is not configured.");
        using var response = await _http.PostAsJsonAsync(url, new { text = NotificationText.Compose(candidate) }, cancellationToken);
        if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500) throw new TransientNotificationException(response.StatusCode);
        response.EnsureSuccessStatusCode();
    }
    public Task SendTestAsync(CancellationToken cancellationToken = default) => SendAsync(new AlertCandidate("test", "info", "", null, 0, "{}", new[] { Id }), cancellationToken);
}

public sealed class SoundChannel : INotificationChannel
{
    public string Id => "sound";
    public Task SendAsync(AlertCandidate candidate, CancellationToken cancellationToken = default) { System.Media.SystemSounds.Exclamation.Play(); return Task.CompletedTask; }
    public Task SendTestAsync(CancellationToken cancellationToken = default) => SendAsync(new AlertCandidate("test", "info", "", null, 0, "{}", new[] { Id }), cancellationToken);
}

internal static class NotifyIconBalloon
{
    private const int NIM_MODIFY = 0x00000001;
    private const int NIF_INFO = 0x00000010;
    private const int NIIF_USER = 0x00000004;
    private const int NIIF_LARGE_ICON = 0x00000020;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATAW lpData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    public static bool TryShow(System.Windows.Forms.NotifyIcon notifyIcon, string title, string text)
    {
        try
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var notifyIconType = typeof(System.Windows.Forms.NotifyIcon);
            var idField = notifyIconType.GetField("_id", flags) ?? notifyIconType.GetField("id", flags);
            var windowField = notifyIconType.GetField("_window", flags) ?? notifyIconType.GetField("window", flags);
            var idValue = idField?.GetValue(notifyIcon);
            int id;
            if (idValue is int intId)
                id = intId;
            else if (idValue is uint uintId)
                id = unchecked((int)uintId);
            else
                return false;

            if (windowField?.GetValue(notifyIcon) is not System.Windows.Forms.NativeWindow window)
                return false;

            var hWnd = window.Handle;
            var iconHandle = notifyIcon.Icon?.Handle ?? IntPtr.Zero;
            if (hWnd == IntPtr.Zero || iconHandle == IntPtr.Zero)
                return false;

            var data = new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = hWnd,
                uID = id,
                uFlags = NIF_INFO,
                szTip = "",
                szInfo = text.Length > 255 ? text.Substring(0, 255) : text,
                szInfoTitle = title.Length > 63 ? title.Substring(0, 63) : title,
                dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON,
                hBalloonIcon = iconHandle
            };
            return Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
        catch
        {
            return false;
        }
    }
}

public sealed class WindowsBalloonChannel : INotificationChannel
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    public string Id => "windows";
    public WindowsBalloonChannel(System.Windows.Forms.NotifyIcon icon) => _icon = icon;
    public Task SendAsync(AlertCandidate candidate, CancellationToken cancellationToken = default)
    {
        var title = NotificationText.Label(candidate.NotificationType);
        var message = NotificationText.ExtractMessage(candidate);
        var bodyText = message ?? " ";
        if (!NotifyIconBalloon.TryShow(_icon, title, bodyText))
        {
            var icon = candidate.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)
                ? System.Windows.Forms.ToolTipIcon.Error
                : candidate.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase)
                    ? System.Windows.Forms.ToolTipIcon.Warning
                    : System.Windows.Forms.ToolTipIcon.Info;
            _icon.ShowBalloonTip(5000, title, bodyText, icon);
        }
        return Task.CompletedTask;
    }
    public Task SendTestAsync(CancellationToken cancellationToken = default) => SendAsync(new AlertCandidate("test", "info", "", null, 0, "{}", new[] { Id }), cancellationToken);
}

public sealed class TransientNotificationException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public TransientNotificationException(HttpStatusCode statusCode) : base("Transient notification failure") => StatusCode = statusCode;
}

public sealed class AlertEngine
{
    public static long StableStateVersion(string? key)
    {
        if (string.IsNullOrEmpty(key)) return 0;
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return BitConverter.ToInt64(hash, 0) & long.MaxValue;
    }

    public static readonly IReadOnlyList<AlertRule> DefaultRules = new[]
    {
        new AlertRule("permission-wait", true, "warning", "PermissionRequest", 60, 300, new[] { "windows", "discord", "slack" }),
        new AlertRule("input-needed", true, "warning", "Notification", 60, 300, new[] { "windows", "discord", "slack" }),
        new AlertRule("authentication-failure", true, "error", "AuthenticationFailure", 300, 900, new[] { "windows", "discord", "slack" }),
        new AlertRule("runtime-unreachable", true, "error", "RuntimeUnreachable", 300, 900, new[] { "windows", "discord", "slack" }),
        new AlertRule("background-failed", true, "error", "BackgroundFailed", 300, 900, new[] { "windows", "discord", "slack" }),
        new AlertRule("recovery-manual", true, "error", "RecoveryManual", 300, 900, new[] { "windows", "discord", "slack" }),
        new AlertRule("recovery-exhausted", true, "error", "RecoveryExhausted", 300, 900, new[] { "windows", "discord", "slack" }),
        new AlertRule("daily-budget-exceeded", true, "error", "DailyBudgetExceeded", 300, 900, new[] { "windows", "discord", "slack" }),
    new AlertRule("daemon-unreachable", true, "error", "DaemonFailure", 300, 900, new[] { "windows", "discord", "slack" }),
        new AlertRule("task-completed", true, "info", "Stop", 10, 60, new[] { "windows", "discord", "slack" }),
        new AlertRule("question-pending", true, "warning", "QuestionPending", 60, 300, new[] { "windows", "discord", "slack" })
};
    public AlertCandidate? FromHook(HookEvent hook, string runtimeId, long stateVersion = 0)
    {
        var rule = DefaultRules.FirstOrDefault(x => x.Enabled
            && x.RuleId is not "authentication-failure" and not "background-failed" and not "daemon-unreachable" and not "runtime-unreachable"
            && string.Equals(x.Condition, hook.EventName, StringComparison.OrdinalIgnoreCase));
        return rule is null ? null : new AlertCandidate(rule.RuleId, rule.Severity, runtimeId, hook.SessionId, stateVersion != 0 ? stateVersion : StableStateVersion(hook.SourceEventId), JsonSerializer.Serialize(hook.Payload), rule.Channels);
    }
    public AlertCandidate? QuestionPending(PendingQuestionRecord question) => new AlertCandidate("question-pending", "warning", question.RuntimeId, question.SessionId, StableStateVersion(question.Id), question.QuestionsJson, new[] { "windows", "discord" });

    // claude daemon statusのexit 1は通常時の応答であり、異常判定に使えないことがPhase 0で判明したため。
    public AlertCandidate? FromSnapshot(RuntimeSnapshot snapshot, long stateVersion = 0)
    {
        var rule = snapshot.Error is not null
            ? DefaultRules.First(x => x.RuleId == "background-failed")
            : !snapshot.AuthStatus.Succeeded
                ? DefaultRules.First(x => x.RuleId == (IsRuntimeUnreachable(snapshot.AuthStatus) ? "runtime-unreachable" : "authentication-failure"))
                    : null;

        return rule is null
            ? null
            : new AlertCandidate(rule.RuleId, rule.Severity, snapshot.RuntimeId, null, stateVersion,
                JsonSerializer.Serialize(snapshot), rule.Channels);
    }

    // A non-zero claude auth status exit code does not always mean the user is logged out: a misconfigured WSL distro name or an unresolvable claude binary produces the same failed exit code but has nothing to do with authentication, and must not be reported as an authentication failure. This happened when AGENTSUPERVISOR_WSL_DISTRO pointed at a nonexistent distro and repeatedly fired false authentication-failure alerts every five minutes.
    private static bool IsRuntimeUnreachable(RuntimeCommandResult authStatus)
    {
        // wsl.exe writes some of its own (non-Linux) error messages as UTF-16LE even though
        // ProcessRunner reads stdout/stderr with the default encoding, which decodes each
        // 2-byte UTF-16 character as two separate chars: the real character followed by a
        // literal NUL. Confirmed on a real machine: "WSL_E_DISTRO_NOT_FOUND" actually arrives
        // as "W\0S\0L\0_\0E\0...", so the substring check below never matched until the NULs
        // are stripped first.
        var text = ((authStatus.Error ?? "") + " " + (authStatus.Output ?? "")).Replace("\0", "");
        return text.Contains("WSL_E_DISTRO_NOT_FOUND", StringComparison.OrdinalIgnoreCase)
            || text.Contains("command not found", StringComparison.OrdinalIgnoreCase)
            || text.Contains("is not recognized as an internal or external command", StringComparison.OrdinalIgnoreCase);
    }

    public int GetCooldownSeconds(string notificationType) =>
        DefaultRules.FirstOrDefault(r => r.RuleId == notificationType)?.CooldownSeconds ?? 60;
}

public sealed class NotificationWorker : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly IServiceProvider _services;
    public NotificationWorker(IServiceProvider services) => _services = services;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Delivery is intentionally isolated from hook ingestion. Applications may supply
            // an INotificationOutbox implementation without coupling the worker to their DB layer.
            var outbox = _services.GetService(typeof(INotificationOutbox)) as INotificationOutbox;
            if (outbox is not null) await outbox.DeliverPendingAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}

public interface INotificationOutbox
{
    void Enqueue(AlertCandidate candidate, int cooldownSeconds);
    Task DeliverPendingAsync(CancellationToken cancellationToken);
}

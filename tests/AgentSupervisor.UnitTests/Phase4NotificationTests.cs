using System.Net;
using AgentSupervisor.Core;
using AgentSupervisor.Infrastructure;
using Xunit;

namespace AgentSupervisor.UnitTests;

public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpStatusCode> _responses;
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    public FakeHttpMessageHandler(params HttpStatusCode[] responses) => _responses = new(responses);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var status = _responses.Count > 0 ? _responses.Dequeue() : HttpStatusCode.OK;
        return new HttpResponseMessage(status);
    }
}

public sealed class Phase4NotificationTests
{
    private static AlertCandidate SampleCandidate(string channel = "discord") =>
        new("permission-wait", "warning", "windows", "session-1", 1, "{}", new[] { channel });

    [Fact]
    public async Task DiscordChannel_posts_expected_payload()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.NoContent);
        var channel = new DiscordWebhookChannel(new HttpClient(handler), () => "https://discord.example/webhook");
        await channel.SendAsync(SampleCandidate());
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("permission-wait", handler.LastRequestBody);
    }

    [Fact]
    public async Task DiscordChannel_429_throws_transient()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.TooManyRequests);
        var channel = new DiscordWebhookChannel(new HttpClient(handler), () => "https://discord.example/webhook");
        await Assert.ThrowsAsync<TransientNotificationException>(() => channel.SendAsync(SampleCandidate()));
    }

    [Fact]
    public async Task DiscordChannel_400_does_not_throw_transient()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.BadRequest);
        var channel = new DiscordWebhookChannel(new HttpClient(handler), () => "https://discord.example/webhook");
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => channel.SendAsync(SampleCandidate()));
        Assert.IsNotType<TransientNotificationException>(ex);
    }

    private sealed class RecordingChannel : INotificationChannel
    {
        public string Id => "discord";
        public int SendCount;
        public Task SendAsync(AlertCandidate candidate, CancellationToken cancellationToken = default) { SendCount++; return Task.CompletedTask; }
        public Task SendTestAsync(CancellationToken cancellationToken = default) => SendAsync(candidate: null!, cancellationToken);
    }

    private sealed class ThrowingTransientChannel : INotificationChannel
    {
        public string Id => "discord";
        public int SendCount;
        public Task SendAsync(AlertCandidate candidate, CancellationToken cancellationToken = default) { SendCount++; throw new TransientNotificationException(HttpStatusCode.ServiceUnavailable); }
        public Task SendTestAsync(CancellationToken cancellationToken = default) => SendAsync(candidate: null!, cancellationToken);
    }

    private static string NewTempDb() => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");

    [Fact]
    public void Enqueue_within_cooldown_is_skipped()
    {
        var file = NewTempDb();
        try
        {
            var cs = "Data Source=" + file;
            new SqliteMigrationRunner().Migrate(cs);
            var outbox = new SqliteNotificationOutbox(cs, Array.Empty<INotificationChannel>());
            outbox.Enqueue(SampleCandidate(), cooldownSeconds: 300);
            outbox.Enqueue(new AlertCandidate("permission-wait", "warning", "windows", "session-1", 2, "{}", new[] { "discord" }), cooldownSeconds: 300);
            using var c = new Microsoft.Data.Sqlite.SqliteConnection(cs);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM notification_outbox";
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public async Task DeliverPendingAsync_sends_via_matching_channel_and_marks_sent()
    {
        var file = NewTempDb();
        try
        {
            var cs = "Data Source=" + file;
            new SqliteMigrationRunner().Migrate(cs);
            var channel = new RecordingChannel();
            var outbox = new SqliteNotificationOutbox(cs, new INotificationChannel[] { channel });
            outbox.Enqueue(SampleCandidate(), cooldownSeconds: 0);
            await outbox.DeliverPendingAsync(CancellationToken.None);
            Assert.Equal(1, channel.SendCount);
            using var c = new Microsoft.Data.Sqlite.SqliteConnection(cs);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT status FROM notification_outbox";
            Assert.Equal("sent", (string)cmd.ExecuteScalar()!);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public async Task DeliverPendingAsync_retries_transient_failures_without_marking_failed_immediately()
    {
        var file = NewTempDb();
        try
        {
            var cs = "Data Source=" + file;
            new SqliteMigrationRunner().Migrate(cs);
            var channel = new ThrowingTransientChannel();
            var outbox = new SqliteNotificationOutbox(cs, new INotificationChannel[] { channel });
            outbox.Enqueue(SampleCandidate(), cooldownSeconds: 0);
            await outbox.DeliverPendingAsync(CancellationToken.None);
            using var c = new Microsoft.Data.Sqlite.SqliteConnection(cs);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT status, attempt_count FROM notification_outbox";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("pending", reader.GetString(0));
            Assert.Equal(1, reader.GetInt32(1));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public void WebhookSecretStore_masks_and_does_not_expose_full_url()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = new LocalAppDataLayout(root);
            var store = new WebhookSecretStore(layout);
            store.Set("discord", "https://discord.example.com/webhook/secret-token-value");
            var suffix = store.GetMaskedSuffix("discord");
            Assert.NotNull(suffix);
            Assert.DoesNotContain("discord.example.com", suffix);
            Assert.Equal("https://discord.example.com/webhook/secret-token-value", store.Get("discord"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}

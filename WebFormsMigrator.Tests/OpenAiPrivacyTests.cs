using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WebFormsMigrator.Models;
using WebFormsMigrator.Services;

namespace WebFormsMigrator.Tests;

public sealed class OpenAiPrivacyTests
{
    [Fact]
    public async Task Responses_request_disables_storage_and_captures_usage()
    {
        var handler = new CaptureHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var service = new OpenAiMigrationService(client, Options.Create(new OpenAiOptions { Model = "test-model" }));
        var batch = new MigrationBatch { Id = "batch", Kind = "pages", Name = "Pages", Files = [new SourceFile("Default.aspx", "<%@ Page %>")] };

        var result = await service.MigrateBatchAsync("Example", "net10.0", batch, ["Default.aspx"], [], new MigrationAnalysis(), "secret", CancellationToken.None);

        using var request = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(request.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal("reframe-webforms-pages", request.RootElement.GetProperty("prompt_cache_key").GetString());
        Assert.Equal(120, result.ProviderUsage!.InputTokens);
        Assert.Equal(20, result.ProviderUsage.CachedInputTokens);
        Assert.Equal(30, result.ProviderUsage.OutputTokens);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var migration = JsonSerializer.Serialize(new { summary = "done", steps = Array.Empty<string>(), files = Array.Empty<object>() });
            var response = JsonSerializer.Serialize(new
            {
                output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = migration } } } },
                usage = new { input_tokens = 120, input_tokens_details = new { cached_tokens = 20 }, output_tokens = 30 }
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}

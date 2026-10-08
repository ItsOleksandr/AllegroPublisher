using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Configuration;

namespace Allegro.Core;

public sealed class AiClient
{
    private readonly AnthropicClient _client;
    private long _inputTokens;
    private long _outputTokens;

    public AiClient()
    {
        var section = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("coresettings.json", optional: false, reloadOnChange: true)
            .Build()
            .GetSection("Ai");

        var apiKey = section["ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new FormatException("No Anthropic API key: set Ai:ApiKey in coresettings.json or ANTHROPIC_API_KEY.");
        }

        Model = string.IsNullOrWhiteSpace(section["Model"]) ? "claude-haiku-5-5" : section["Model"]!;
        _client = new AnthropicClient { ApiKey = apiKey };
    }

    public string Model { get; }

    public long InputTokens => Interlocked.Read(ref _inputTokens);

    public long OutputTokens => Interlocked.Read(ref _outputTokens);

    public async Task<string> CompleteAsync(string system, string content)
    {
        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 4096,
            System = system,
            Messages = [new() { Role = Role.User, Content = content }],
        });

        Interlocked.Add(ref _inputTokens, response.Usage.InputTokens);
        Interlocked.Add(ref _outputTokens, response.Usage.OutputTokens);

        if (response.StopReason == "refusal")
        {
            throw new InvalidOperationException("the model declined this product.");
        }
        if (response.StopReason == "max_tokens")
        {
            throw new InvalidOperationException("the answer was cut off at the token limit.");
        }

        return string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
    }
}

/*
 * LlmChatService.cs
 * A multi-provider LLM chat service with conversation history support.
 * Supports Azure OpenAI and OpenAI-compatible APIs (LiteLLM).
 * 
 * Usage:
 * 1. Register in Program.cs: builder.Services.AddScoped<LlmChatService>();
 * 2. Configure appsettings.json with LlmProvider settings.
 */

using Azure;
using Azure.AI.OpenAI;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace LlmPocApp.Services;

/// <summary>
/// Configuration for the LLM provider.
/// </summary>
public class LlmProviderSettings
{
    /// <summary>
    /// Provider type: "AzureOpenAI" or "OpenAI" (for LiteLLM and other OpenAI-compatible APIs).
    /// </summary>
    public string Provider { get; set; } = "AzureOpenAI";
    
    /// <summary>
    /// Azure OpenAI endpoint or LiteLLM base URL.
    /// </summary>
    public string? Endpoint { get; set; }
    
    /// <summary>
    /// API key for authentication.
    /// </summary>
    public string? ApiKey { get; set; }
    
    /// <summary>
    /// Deployment name (Azure OpenAI) or model name (OpenAI/LiteLLM).
    /// </summary>
    public string DeploymentName { get; set; } = "gpt-5-chat";

    /// <summary>
    /// Default temperature for requests.
    /// </summary>
    public float Temperature { get; set; } = 0.7f;

    /// <summary>
    /// Default max tokens for requests.
    /// </summary>
    public int MaxTokens { get; set; } = 2000;

    /// <summary>
    /// Pricing configuration for cost estimation.
    /// </summary>
    public PricingSettings Pricing { get; set; } = new PricingSettings();
}

public class PricingSettings
{
    public decimal InputPer1K { get; set; } = 0.005m;   // Default: GPT-4o pricing
    public decimal OutputPer1K { get; set; } = 0.015m;  // Default: GPT-4o pricing
}

/// <summary>
/// Represents a message in the conversation history.
/// </summary>
public class ConversationMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

/// <summary>
/// A multi-provider LLM chat service with conversation history.
/// </summary>
public class LlmChatService
{
    private ChatClient _chatClient;
    private readonly ILogger<LlmChatService> _logger;
    private readonly LlmProviderSettings _settings;
    private readonly List<ConversationMessage> _history = new();
    private string _systemPrompt = "You are a helpful assistant.";

    // Session stats
    private int _totalInputTokens = 0;
    private int _totalOutputTokens = 0;
    private decimal _totalCost = 0;
    private int _totalRequests = 0;
    private TimeSpan _totalDuration = TimeSpan.Zero;

    public int TotalInputTokens => _totalInputTokens;
    public int TotalOutputTokens => _totalOutputTokens;
    public decimal TotalCost => _totalCost;
    public string? LastUsedModel { get; private set; }
    
    public TimeSpan AverageLatency => _totalRequests > 0 
        ? _totalDuration / _totalRequests 
        : TimeSpan.Zero;

    public LlmChatService(IConfiguration config, ILogger<LlmChatService> logger)
    {
        _logger = logger;

        _settings = config.GetSection("LlmProvider").Get<LlmProviderSettings>() 
            ?? new LlmProviderSettings();

        _chatClient = CreateChatClient(_settings);
        
        _logger.LogInformation("LlmChatService initialized with provider: {Provider}", _settings.Provider);
    }

    /// <summary>
    /// Updates the active model/deployment.
    /// </summary>
    public void UpdateModel(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName) || _settings.DeploymentName == modelName)
            return;

        _logger.LogInformation("Switching model from {Old} to {New}", _settings.DeploymentName, modelName);
        _settings.DeploymentName = modelName;
        _chatClient = CreateChatClient(_settings);
    }

    private ChatClient CreateChatClient(LlmProviderSettings settings)
    {
        if (string.IsNullOrEmpty(settings.ApiKey))
            throw new InvalidOperationException("Missing LlmProvider:ApiKey in configuration.");

        if (settings.Provider.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase))
        {
            // Azure OpenAI
            if (string.IsNullOrEmpty(settings.Endpoint))
                throw new InvalidOperationException("Missing LlmProvider:Endpoint for Azure OpenAI.");

            var client = new AzureOpenAIClient(
                new Uri(settings.Endpoint), 
                new AzureKeyCredential(settings.ApiKey));
            
            return client.GetChatClient(settings.DeploymentName);
        }
        else
        {
            // OpenAI-compatible API (LiteLLM, etc.)
            var options = new OpenAIClientOptions();
            
            if (!string.IsNullOrEmpty(settings.Endpoint))
            {
                options.Endpoint = new Uri(settings.Endpoint);
            }

            var client = new OpenAIClient(
                new ApiKeyCredential(settings.ApiKey), 
                options);
            
            return client.GetChatClient(settings.DeploymentName);
        }
    }

    /// <summary>
    /// Gets the current system prompt.
    /// </summary>
    public string SystemPrompt => _systemPrompt;

    /// <summary>
    /// Sets the system prompt for the conversation.
    /// </summary>
    public void SetSystemPrompt(string systemPrompt)
    {
        _systemPrompt = systemPrompt;
        _logger.LogDebug("System prompt updated.");
    }

    /// <summary>
    /// Gets the conversation history (read-only).
    /// </summary>
    public IReadOnlyList<ConversationMessage> History => _history.AsReadOnly();

    /// <summary>
    /// Clears the conversation history and resets token stats.
    /// </summary>
    public void ClearHistory()
    {
        _history.Clear();
        _totalInputTokens = 0;
        _totalOutputTokens = 0;
        _totalCost = 0;
        _totalRequests = 0;
        _totalDuration = TimeSpan.Zero;
        _logger.LogDebug("Conversation history and stats cleared.");
    }

    /// <summary>
    /// Sends a request to the LLM without modifying the conversation history.
    /// Useful for background tasks like evaluation or prompt generation.
    /// </summary>
    /// <summary>
    /// Sends a request to the LLM without modifying the conversation history.
    /// Useful for background tasks like evaluation or prompt generation.
    /// </summary>
    public async Task<string> GetRawResponseAsync(string systemPrompt, string userPrompt, string? modelOverride = null)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        var options = new ChatCompletionOptions();
        
        string targetModel = modelOverride ?? _settings.DeploymentName;

        // Connect O1/GPT-5 model compatibility: avoid unsupported parameters (max_tokens, temperature)
        bool isAdvancedModel = targetModel.Contains("o1", StringComparison.OrdinalIgnoreCase) 
                               || targetModel.Contains("gpt-5", StringComparison.OrdinalIgnoreCase);
        
        if (!isAdvancedModel)
        {
            options.Temperature = _settings.Temperature;
            options.MaxOutputTokenCount = _settings.MaxTokens;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // If overriding model, we need a new client or rely on the client to support model param override?
        // Azure OpenAI Client is tied to DeploymentName. So we must recreate client if model is different.
        ChatClient clientToUse = _chatClient;
        if (modelOverride != null && !string.Equals(modelOverride, _settings.DeploymentName, StringComparison.OrdinalIgnoreCase))
        {
             // Create a temporary client for this request
             var tempSettings = new LlmProviderSettings 
             { 
                 Provider = _settings.Provider,
                 Endpoint = _settings.Endpoint,
                 ApiKey = _settings.ApiKey,
                 DeploymentName = modelOverride,
                 Pricing = _settings.Pricing,
                 Temperature = _settings.Temperature,
                 MaxTokens = _settings.MaxTokens
             };
             clientToUse = CreateChatClient(tempSettings);
        }

        var response = await clientToUse.CompleteChatAsync(messages, options);
        sw.Stop();
        
        // Track usage (optional, but good for billing)
        TrackUsage(response.Value.Usage, response.Value.Model, sw.Elapsed);

        return response.Value.Content[0].Text;
    }

    /// <summary>
    /// Sends a message and gets the assistant's response.
    /// The conversation history is automatically maintained.
    /// </summary>
    public async Task<string> SendMessageAsync(string userMessage)
    {
        try
        {
            // Add user message to history
            _history.Add(new ConversationMessage
            {
                Role = "user",
                Content = userMessage
            });

            // Build messages list for API call
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(_systemPrompt)
            };

            foreach (var msg in _history)
            {
                if (msg.Role == "user")
                    messages.Add(new UserChatMessage(msg.Content));
                else if (msg.Role == "assistant")
                    messages.Add(new AssistantChatMessage(msg.Content));
            }

            // Call the API
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await _chatClient.CompleteChatAsync(messages);
            sw.Stop();
            
            var assistantMessage = response.Value.Content[0].Text;
            
            // Track Usage
            TrackUsage(response.Value.Usage, response.Value.Model, sw.Elapsed);

            // Add assistant response to history
            _history.Add(new ConversationMessage
            {
                Role = "assistant",
                Content = assistantMessage
            });

            return assistantMessage;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM chat request failed.");
            throw;
        }
    }

    /// <summary>
    /// Sends a message with a one-off system prompt (does not affect stored history).
    /// Useful for single-turn queries.
    /// </summary>
    public async Task<string> SendSingleMessageAsync(string userMessage, string? systemPrompt = null)
    {
        try
        {
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt ?? _systemPrompt),
                new UserChatMessage(userMessage)
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await _chatClient.CompleteChatAsync(messages);
            sw.Stop();

            TrackUsage(response.Value.Usage, response.Value.Model, sw.Elapsed);
            
            return response.Value.Content[0].Text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM single message request failed.");
            throw;
        }
    }

    /// <summary>
    /// Sends a message and expects a JSON response.
    /// </summary>
    public async Task<T?> SendMessageJsonAsync<T>(string userMessage, string? systemPrompt = null)
    {
        try
        {
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt ?? _systemPrompt),
                new UserChatMessage(userMessage)
            };

            var options = new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await _chatClient.CompleteChatAsync(messages, options);
            sw.Stop();

            TrackUsage(response.Value.Usage, response.Value.Model, sw.Elapsed);
            
            var json = response.Value.Content[0].Text;

            var serializerOptions = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            serializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

            return System.Text.Json.JsonSerializer.Deserialize<T>(json, serializerOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM JSON request failed.");
            throw;
        }
    }

    // Standard Pricing Table (USD per 1K tokens)
    private static readonly Dictionary<string, (decimal Input, decimal Output)> _pricingTable = new()
    {
        // GPT-4o (The flagship)
        { "gpt-4o", (0.005m, 0.015m) },
        
        // GPT-4o Mini (Cost effective)
        { "gpt-4o-mini", (0.00015m, 0.0006m) },
        
        // GPT-4 Turbo
        { "gpt-4-turbo", (0.01m, 0.03m) },
        
        // GPT-3.5 Turbo
        { "gpt-3.5-turbo", (0.0005m, 0.0015m) }
    };

    private void TrackUsage(ChatTokenUsage usage, string? actualModelName, TimeSpan duration)
    {
        if (usage == null) return;

        LastUsedModel = actualModelName;
        int input = usage.InputTokenCount;
        int output = usage.OutputTokenCount;

        // Determine price based on actual model name (e.g., "gpt-4o-2024-05-13")
        var (inputPrice, outputPrice) = GetPricing(actualModelName);

        decimal cost = (input / 1000m * inputPrice) + 
                       (output / 1000m * outputPrice);

        _totalInputTokens += input;
        _totalOutputTokens += output;
        _totalCost += cost;
        
        _totalRequests++;
        _totalDuration += duration;

        // Log to console
        _logger.LogInformation(
            "💰 Stats: In={In}, Out={Out}, Time={Time}s | Cost=${Cost:0.0000} | Avg Latency={Avg:0.0}s | Session=${Total:0.0000}", 
            input, output, duration.TotalSeconds.ToString("0.0"), cost, AverageLatency.TotalSeconds, _totalCost);
    }

    private (decimal Input, decimal Output) GetPricing(string? modelName)
    {
        // 1. Check if user configured explicit override in appsettings
        // If the setting is NOT default (0.005/0.015 is default in PricingSettings class, need to be careful)
        // Ideally, we prefer the mapping if available. 
        
        if (string.IsNullOrEmpty(modelName)) 
            return (_settings.Pricing.InputPer1K, _settings.Pricing.OutputPer1K);

        modelName = modelName.ToLowerInvariant();

        // 2. Try exact or partial match from table
        foreach (var key in _pricingTable.Keys)
        {
            if (modelName.Contains(key))
            {
                return _pricingTable[key];
            }
        }

        // 3. Fallback to configured default (GPT-4o pricing by default)
        return (_settings.Pricing.InputPer1K, _settings.Pricing.OutputPer1K);
    }
}

namespace AiReap.Infrastructure.Ai;

public class OllamaOptions
{
    public const string SectionName = "Ai:Ollama";

    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "llama3.1:8b";

    // Ollama's own default context window (2048 tokens on most models unless the Modelfile says
    // otherwise) is too small for this app's larger requirement-source documents - the same
    // headroom problem AnthropicOptions.MaxTokens documents. Without raising this, a large
    // document silently truncates mid-generation well before OllamaAiChatClient's done_reason
    // check can catch it as anything other than "length".
    public int NumCtx { get; set; } = 8192;

    // The first call after Ollama (re)loads a model pays a one-off model-load cost that can run
    // into tens of seconds on modest hardware - longer than the default HttpClient timeout.
    public int TimeoutSeconds { get; set; } = 120;
}

using System;
using System.Threading.Tasks;

namespace LlmPocApp.Services
{
    public class PromptGenerationService
    {
        private readonly LlmChatService _chatService;

        public PromptGenerationService(LlmChatService chatService)
        {
            _chatService = chatService;
        }

        public async Task<string> GenerateSystemPromptAsync(string goal, string expectedInput, string expectedOutput)
        {
            var metaPrompt = @"You are an expert Prompt Engineer. 
Your task is to write a high-quality System Prompt for an AI Assistant based on the user's requirements.

Guidelines:
1. The System Prompt should clearly define the assistant's persona, tone, and constraints.
2. It should include few-shot examples if helpful (based on the provided input/output).
3. Output ONLY the raw system prompt text. Do not include markdown code fencing (```) or introductory text.
4. Keep it concise but effective.
5. If the user input is in Traditional Chinese, the System Prompt MUST be output in Traditional Chinese.";

            var userRequest = $@"
Requirements:
- Goal: {goal}
- The assistant receives inputs like: ""{expectedInput}""
- And should respond like: ""{expectedOutput}""

Please write the System Prompt.";

            return await _chatService.SendSingleMessageAsync(userRequest, metaPrompt);
        }
    }
}

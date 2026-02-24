
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.ObjectPool;
using Newtonsoft.Json;
using Nexa.Adapter.Models;
using Nexa.Adapter.Services;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Nexa.Adapter.Infrastructure.LLM.AwsResponseParser;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Nexa.Adapter.Infrastructure.LLM
{
    public class BedrockProvider : ILLMProvider
    {
        private readonly AmazonBedrockRuntimeClient _client;
        private readonly string _modelId;
        private readonly ILLMResponseParser _parser;
        private readonly IEnumerable<ITool> _tools;
        public BedrockProvider(IConfiguration config, ILLMResponseParser parser, IEnumerable<ITool> tools)
        {
            _modelId = config["LLM:Bedrock:ModelId"];
            var credentials = new SessionAWSCredentials(
            Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID"),
            Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY"),
            Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN")
         );
          
            _client = new AmazonBedrockRuntimeClient(
                credentials,
                Amazon.RegionEndpoint.USWest2
            );
            _parser = parser;
            _tools= tools ?? new List<ITool>();
        }
        private async Task<string> InvokeLLM(List<LlmMessage> messages, IEnumerable<ITool>? toolSpecs = null)
        {
            var messagList = new List<object>();

            var filteredMessages = messages.Where(x => x.Role != Role.System).ToList();
            foreach (var item in filteredMessages)
            {
                messagList.Add(new
                {
                    role = item.Role.ToString().ToLower(),
                    content = new[]
                    {
                        new { text = item.Content}
                    }
                });
            }
            if (!messagList.Any())
            {
                messagList.Add(new
                {
                    role = "user",
                    content = new[]
                    {
                        new { text = "please follow the given instructions in the system prompt and provide output in given format"}
                    }
                });
            }
            List<Object> lstTools= new List<Object>();
            if (toolSpecs != null && toolSpecs.Any())
            {
                foreach (var item in toolSpecs)
                {
                    lstTools.Add(new
                    {
                        toolSpec = new
                        {
                            name = item.Name,
                            description = item.Description,
                            inputSchema = new { json = item.InputSchema }
                        }
                    });
                }
            }
            var toolConfig = new
            {
                tools = lstTools
            };
            Object requestBody;
            if (toolConfig.tools.Any())
            {
                requestBody = new
                {
                    system = new[] { new { text = messages.Where(x => x.Role == Role.System).FirstOrDefault().Content } },
                    messages = messagList,
                    toolConfig = toolConfig,
                    inferenceConfig = new
                    {
                        maxTokens = 1000,
                        temperature = 0.7,
                        topP = 0.9
                    }
                };
            }
            else
            {
                 requestBody = new
                {
                    system = new[] { new { text = messages.Where(x => x.Role == Role.System).FirstOrDefault().Content } },
                    messages = messagList,
                    inferenceConfig = new
                    {
                        maxTokens = 1000,
                        temperature = 0.7,
                        topP = 0.9
                    }
                };
            }
                

            var request = new InvokeModelRequest
            {
                ModelId = _modelId,
                ContentType = "application/json",
                Accept = "application/json",
                Body = new MemoryStream(
                    Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(requestBody)))
            };

            var response = await _client.InvokeModelAsync(request);

            using var reader = new StreamReader(response.Body);
            var llmResult = await reader.ReadToEndAsync();
            return llmResult;
            
        }
        public async Task<LlmAnalysisResponse>  Analyze(List<LlmMessage> messages)
        {
            string llmResponse= await InvokeLLM(messages);
            var obj= _parser.Parse<LlmAnalysisResponse>(llmResponse);
            return obj;
        }

        public async Task<NexaLlmResponse> CompleteChat(List<LlmMessage> messages, IEnumerable<ITool>? toolSpecs = null)
        {
            const int maxRounds = 5;

            for (int round = 0; round < maxRounds; round++)
            {
                // ✅ Always include toolSpecs so the model can request tools again
                string llmResponse = await InvokeLLM(messages, toolSpecs);

                var (toolUse, text) = _parser.ExtractToolOrText(llmResponse);

                // ✅ If model requested tools, execute them and continue loop
                if (toolUse != null && toolUse.Any())
                {
                    var toolResults = new List<ToolResult>();

                    foreach (var toolCall in toolUse)
                    {
                        var tool = _tools.FirstOrDefault(t =>
                            string.Equals(t.Name, toolCall.Name, StringComparison.OrdinalIgnoreCase));

                        if (tool == null)
                        {
                            toolResults.Add(new ToolResult { ToolName = toolCall.Name, Output = "Tool not found" });
                            continue;
                        }

                        // ✅ FIX: tool input may contain numbers/bools; don't deserialize to Dictionary<string,string>
                        var argsObj = JsonConvert.DeserializeObject<Dictionary<string, object>>(toolCall.Input ?? "{}")
                                     ?? new Dictionary<string, object>();

                        var inputList = argsObj.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? "");

                        toolResults.Add(await tool.ExecuteAsync(new ToolCall
                        {
                            ToolName = toolCall.Name,
                            Args = inputList
                        }));
                    }

                    // Keep conversation context for the model
                    messages.Add(new LlmMessage { Role = Role.Assistant, Content = JsonSerializer.Serialize(toolUse) });

                    // Strongly instruct model to now return final JSON object
                    messages.Add(new LlmMessage
                    {
                        Role = Role.Assistant,
                        Content =
                            "TOOL_RESULTS:\n" + JsonSerializer.Serialize(toolResults) +
                            "\n\nNow respond ONLY with a single JSON object that matches NexaLlmResponse schema. Do NOT return an array."
                    });

                    continue; // next round (model might request another tool or return final JSON)
                }

                // ✅ Final answer path (no toolUse)
                var cleaned = ExtractJsonObject(text);

                cleaned = _parser.PrependThinkingToResponse(cleaned);

                var obj = JsonConvert.DeserializeObject<NexaLlmResponse>(cleaned);
                if (obj == null) throw new InvalidOperationException("Final response deserialized to null.");
                return obj;
            }

            throw new InvalidOperationException($"Exceeded max tool rounds ({maxRounds}).");
        }

        private static string ExtractJsonObject(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("Expected JSON object but model returned empty text.");

            var s = text.Replace("```json", "", StringComparison.OrdinalIgnoreCase)
                        .Replace("```", "")
                        .Trim();

            // Remove <thinking> blocks if present
            s = System.Text.RegularExpressions.Regex.Replace(
                s, @"<thinking>.*?</thinking>", "",
                System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase
            ).Trim();

            // If model returned array here, that's NOT a NexaLlmResponse object — throw a clear error
            var t = s.TrimStart();
            if (t.StartsWith("["))
                throw new InvalidOperationException("Model returned a JSON array when a NexaLlmResponse JSON object was expected:\n" + s);

            // If already an object
            if (t.StartsWith("{")) return s;

            // Otherwise, extract first JSON object from mixed text
            int start = s.IndexOf('{');
            if (start < 0) throw new InvalidOperationException("No JSON object found in model output:\n" + s);

            int depth = 0;
            for (int i = start; i < s.Length; i++)
            {
                if (s[i] == '{') depth++;
                else if (s[i] == '}') depth--;

                if (depth == 0)
                    return s.Substring(start, i - start + 1).Trim();
            }

            throw new InvalidOperationException("Unbalanced JSON braces in model output:\n" + s);
        }        
    }

    public class AwsResponseParser : ILLMResponseParser
    {
        private static readonly Regex ThinkingRegex =
        new Regex(@"<thinking>(.*?)</thinking>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        public string PrependThinkingToResponse(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return rawText;

            // 1. Extract <thinking> block
            var thinkingMatch = ThinkingRegex.Match(rawText);
            string thinkingContent = string.Empty;

            if (thinkingMatch.Success)
            {
                thinkingContent = thinkingMatch.Groups[1].Value.Trim();
                rawText = ThinkingRegex.Replace(rawText, "").Trim();
            }

            // 2. Parse remaining JSON
            var jsonNode = JsonNode.Parse(rawText) as JsonObject;
            if (jsonNode == null || !jsonNode.ContainsKey("response"))
                return rawText;

            // 3. Prepend thinking to response
            var originalResponse = jsonNode["response"]?.GetValue<string>() ?? string.Empty;

            if (!string.IsNullOrEmpty(thinkingContent))
            {
                jsonNode["response"] =
                    $"[Analyst Reasoning]\n{thinkingContent}\n\n[Conclusion]\n{originalResponse}";
            }

            // 4. Return updated JSON
            return jsonNode.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        public T Parse<T>(string rawResponse, string? extractMethod = "ExtractText")
        {
            //var jsonString = extractMethod== "ExtractToolCall" ? ExtractToolCall(rawResponse) : ExtractText(rawResponse);
            var jsonString = ExtractToolOrText(rawResponse);
            var (toolUse, text) = ExtractToolOrText(rawResponse);
             string cleanedJson = text
            .Replace("```json", "", StringComparison.OrdinalIgnoreCase)
            .Replace("```", "")
            .Trim();
            var obj = JsonConvert.DeserializeObject<T>(cleanedJson);
            return obj;
        }
        //public static string ExtractToolOrText(string json)
        //{
        //    string extractedResponse = "";
        //    var root = JsonNode.Parse(json);

        //    var contentArray = root?["output"]?["message"]?["content"]?.AsArray();
        //    if (contentArray == null || contentArray.Count == 0)
        //        return "";

        //    foreach (var item in contentArray)
        //    {
        //        // Case 1: Tool invocation
        //        if (item?["toolUse"] != null)
        //        {
        //            extractedResponse = item["toolUse"].GetValue<string>()!;
        //            //return (
        //            //    new ToolUse
        //            //    {
        //            //        Name = tool["name"]!.GetValue<string>(),
        //            //        ToolUseId = tool["toolUseId"]!.GetValue<string>(),
        //            //        Input = tool["input"]!.ToJsonString()
        //            //    },
        //            //    null
        //            //);
        //        }

        //        // Case 2: Plain text response
        //        if (item?["text"] != null)
        //        {
        //            extractedResponse= item["text"]!.GetValue<string>();
        //        }
        //    }

        //    return extractedResponse;
        //}
        public class ToolUse
        {
            public string Name { get; set; } = string.Empty;
            public string ToolUseId { get; set; } = string.Empty;

            // Keep input flexible (tools differ)
            public string Input { get; set; } = string.Empty;
        }

        public (List<ToolUse>? toolUse, string? text) ExtractToolOrText(string json)
        {
            List<ToolUse> lstTools = new List<ToolUse>();
            string text = "";
            var root = JsonNode.Parse(json);

            var contentArray = root?["output"]?["message"]?["content"]?.AsArray();
            if (contentArray == null || contentArray.Count == 0)
                return (lstTools, text);

            foreach (var item in contentArray)
            {
                // Case 1: Tool invocation
                if (item?["toolUse"] != null)
                {
                    var tool = item["toolUse"]!;
                    lstTools.Add(
                        new ToolUse
                        {
                            Name = tool["name"]!.GetValue<string>(),
                            ToolUseId = tool["toolUseId"]!.GetValue<string>(),
                            Input = tool["input"]!.ToJsonString()
                        });
                }

                // Case 2: Plain text response
                if (item?["text"] != null)
                {
                    text= item["text"]!.GetValue<string>();
                }
            }

            // ✅ Fallback: tool call list returned as TEXT (JSON array) instead of toolUse blocks
            if (lstTools.Count == 0 && !string.IsNullOrWhiteSpace(text))
            {
                var trimmed = text.TrimStart();

                // looks like JSON array
                if (trimmed.StartsWith("["))
                {
                    try
                    {
                        var calls = JsonConvert.DeserializeObject<List<ToolUseText>>(text);

                        if (calls != null && calls.Any() && calls.All(c => !string.IsNullOrWhiteSpace(c.Name)))
                        {
                            lstTools.AddRange(calls.Select(c => new ToolUse
                            {
                                Name = c.Name,
                                ToolUseId = c.ToolUseId,
                                Input = c.Input
                            }));

                            text = ""; // clear text because it was actually tool requests
                        }
                    }
                    catch
                    {
                        // ignore - it wasn't tool calls
                    }
                }
            }


            return (lstTools, text);
        }


        private class ToolUseText
        {
            [JsonProperty("Name")] public string Name { get; set; } = "";
            [JsonProperty("ToolUseId")] public string ToolUseId { get; set; } = "";
            [JsonProperty("Input")] public string Input { get; set; } = "";
        }


        private static string ExtractText(string json)
        {
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement
                .GetProperty("output")
                .GetProperty("message")
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString()!;
        }
        public string ExtractToolCall(string json)
        {
            var responseObj = JsonConvert.DeserializeObject<object>(json);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement
                .GetProperty("output")
                .GetProperty("message")
                .GetProperty("content")[0]
                .GetProperty("toolUse")
                .GetString()!;
        }
    }
}

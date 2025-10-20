// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Hosting.OpenAI.Tests;

namespace Microsoft.Agents.AI.Hosting.OpenAI.UnitTests;

/// <summary>
/// Conformance tests for OpenAI Responses API implementation behavior.
/// Tests use real API traces to ensure our implementation produces responses
/// that match OpenAI's wire format when processing actual requests through the server.
/// For pure serialization/deserialization tests, see OpenAIResponsesSerializationTests.
/// </summary>
public sealed class OpenAIResponsesConformanceTests : ConformanceTestBase
{
    [Fact]
    public async Task BasicRequestResponseAsync()
    {
        // Arrange
        string requestJson = LoadTraceFile("basic/request.json");
        using var expectedResponseDoc = LoadTraceDocument("basic/response.json");
        var expectedResponse = expectedResponseDoc.RootElement;

        // Get the expected response text from the trace to use as mock response
        string expectedText = expectedResponse.GetProperty("output")[0]
            .GetProperty("content")[0]
            .GetProperty("text").GetString()!;

        HttpClient client = await this.CreateTestServerAsync("basic-agent", "You are a helpful assistant.", expectedText);

        // Act
        HttpResponseMessage httpResponse = await this.SendRequestAsync(client, "basic-agent", requestJson);
        using var responseDoc = await ParseResponseAsync(httpResponse);
        var response = responseDoc.RootElement;

        // Parse the request to verify it was sent correctly
        using var requestDoc = JsonDocument.Parse(requestJson);
        var request = requestDoc.RootElement;

        // Assert - Verify request was properly formatted (structure check)
        AssertJsonPropertyEquals(request, "model", "gpt-4o-mini");
        AssertJsonPropertyExists(request, "input");
        AssertJsonPropertyEquals(request, "max_output_tokens", 100);
        var input = request.GetProperty("input");
        Assert.Equal(JsonValueKind.String, input.ValueKind);
        Assert.Equal("Hello, how are you?", input.GetString());

        // Assert - Response metadata (IDs and timestamps are dynamic, just verify structure)
        AssertJsonPropertyExists(response, "id");
        AssertJsonPropertyEquals(response, "object", "response");
        AssertJsonPropertyExists(response, "created_at");
        AssertJsonPropertyEquals(response, "status", "completed");
        var id = response.GetProperty("id").GetString();
        Assert.NotNull(id);
        Assert.StartsWith("resp_", id);
        var createdAt = response.GetProperty("created_at").GetInt64();
        Assert.True(createdAt > 0, "created_at should be a positive unix timestamp");

        // Assert - Response model
        AssertJsonPropertyExists(response, "model");
        var model = response.GetProperty("model").GetString();
        Assert.NotNull(model);
        Assert.StartsWith("gpt-4o-mini", model);

        // Assert - Output array structure
        AssertJsonPropertyExists(response, "output");
        var output = response.GetProperty("output");
        Assert.Equal(JsonValueKind.Array, output.ValueKind);
        Assert.True(output.GetArrayLength() > 0, "Output array should not be empty");

        // Assert - Message structure
        var firstItem = output[0];
        AssertJsonPropertyExists(firstItem, "id");
        AssertJsonPropertyEquals(firstItem, "type", "message");
        AssertJsonPropertyEquals(firstItem, "status", "completed");
        AssertJsonPropertyEquals(firstItem, "role", "assistant");
        AssertJsonPropertyExists(firstItem, "content");
        var messageId = firstItem.GetProperty("id").GetString();
        Assert.NotNull(messageId);
        Assert.StartsWith("msg_", messageId);

        // Assert - Content array structure
        var content = firstItem.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.True(content.GetArrayLength() > 0, "Content array should not be empty");

        // Assert - Text content structure (verify content matches expected)
        var firstContent = content[0];
        AssertJsonPropertyEquals(firstContent, "type", "output_text");
        AssertJsonPropertyExists(firstContent, "text");
        AssertJsonPropertyExists(firstContent, "annotations");
        AssertJsonPropertyExists(firstContent, "logprobs");
        var text = firstContent.GetProperty("text").GetString();
        Assert.NotNull(text);
        Assert.Equal(expectedText, text); // Verify actual content matches expected
        Assert.Equal(JsonValueKind.Array, firstContent.GetProperty("annotations").ValueKind);
        Assert.Equal(JsonValueKind.Array, firstContent.GetProperty("logprobs").ValueKind);

        // Assert - Usage statistics
        AssertJsonPropertyExists(response, "usage");
        var usage = response.GetProperty("usage");
        AssertJsonPropertyExists(usage, "input_tokens");
        AssertJsonPropertyExists(usage, "output_tokens");
        AssertJsonPropertyExists(usage, "total_tokens");
        var inputTokens = usage.GetProperty("input_tokens").GetInt32();
        var outputTokens = usage.GetProperty("output_tokens").GetInt32();
        var totalTokens = usage.GetProperty("total_tokens").GetInt32();
        Assert.True(inputTokens > 0, "input_tokens should be positive");
        Assert.True(outputTokens > 0, "output_tokens should be positive");
        Assert.Equal(inputTokens + outputTokens, totalTokens);

        // Assert - Usage details
        AssertJsonPropertyExists(usage, "input_tokens_details");
        var inputDetails = usage.GetProperty("input_tokens_details");
        AssertJsonPropertyExists(inputDetails, "cached_tokens");
        AssertJsonPropertyExists(usage, "output_tokens_details");
        var outputDetails = usage.GetProperty("output_tokens_details");
        AssertJsonPropertyExists(outputDetails, "reasoning_tokens");
        Assert.True(inputDetails.GetProperty("cached_tokens").GetInt32() >= 0);
        Assert.True(outputDetails.GetProperty("reasoning_tokens").GetInt32() >= 0);

        // Assert - Optional fields
        AssertJsonPropertyExists(response, "parallel_tool_calls");
        AssertJsonPropertyExists(response, "tools");
        AssertJsonPropertyExists(response, "temperature");
        AssertJsonPropertyExists(response, "top_p");
        AssertJsonPropertyExists(response, "metadata");
        Assert.Equal(JsonValueKind.True, response.GetProperty("parallel_tool_calls").ValueKind);
        Assert.Equal(JsonValueKind.Array, response.GetProperty("tools").ValueKind);
        Assert.Equal(JsonValueKind.Number, response.GetProperty("temperature").ValueKind);
        Assert.Equal(JsonValueKind.Number, response.GetProperty("top_p").ValueKind);
        Assert.Equal(JsonValueKind.Object, response.GetProperty("metadata").ValueKind);

        // Assert - Error fields are null
        AssertJsonPropertyExists(response, "error");
        AssertJsonPropertyExists(response, "incomplete_details");
        Assert.Equal(JsonValueKind.Null, response.GetProperty("error").ValueKind);
        Assert.Equal(JsonValueKind.Null, response.GetProperty("incomplete_details").ValueKind);

        // Assert - No previous response ID
        AssertJsonPropertyExists(response, "previous_response_id");
        Assert.Equal(JsonValueKind.Null, response.GetProperty("previous_response_id").ValueKind);

        // Assert - Service tier and store
        AssertJsonPropertyExists(response, "service_tier");
        var serviceTier = response.GetProperty("service_tier").GetString();
        Assert.NotNull(serviceTier);
        Assert.True(serviceTier == "default" || serviceTier == "auto",
            $"service_tier should be 'default' or 'auto', got '{serviceTier}'");
        AssertJsonPropertyExists(response, "store");
        Assert.Equal(JsonValueKind.True, response.GetProperty("store").ValueKind);
    }

    [Fact]
    public async Task ConversationRequestResponseAsync()
    {
        // Arrange
        string requestJson = LoadTraceFile("conversation/request.json");
        using var expectedResponseDoc = LoadTraceDocument("conversation/response.json");
        var expectedResponse = expectedResponseDoc.RootElement;

        // Get the expected response text
        string expectedText = expectedResponse.GetProperty("output")[0]
            .GetProperty("content")[0]
            .GetProperty("text").GetString()!;

        HttpClient client = await this.CreateTestServerAsync("conversation-agent", "You are a helpful assistant.", expectedText);

        // Act
        HttpResponseMessage httpResponse = await this.SendRequestAsync(client, "conversation-agent", requestJson);
        using var responseDoc = await ParseResponseAsync(httpResponse);
        var response = responseDoc.RootElement;

        // Parse the request
        using var requestDoc = JsonDocument.Parse(requestJson);
        var request = requestDoc.RootElement;

        // Assert - Request has previous_response_id (structure verification)
        AssertJsonPropertyExists(request, "previous_response_id");
        var previousResponseId = request.GetProperty("previous_response_id").GetString();
        Assert.NotNull(previousResponseId);
        Assert.StartsWith("resp_", previousResponseId);
        Assert.NotEmpty(previousResponseId);

        // Assert - Request structure
        AssertJsonPropertyEquals(request, "model", "gpt-4o-mini");
        AssertJsonPropertyExists(request, "input");
        AssertJsonPropertyExists(request, "previous_response_id");
        AssertJsonPropertyExists(request, "max_output_tokens");
        var input = request.GetProperty("input");
        Assert.Equal(JsonValueKind.String, input.ValueKind);

        // Assert - Response should have previous_response_id field preserved from request
        AssertJsonPropertyExists(response, "previous_response_id");
        var responsePreviousId = response.GetProperty("previous_response_id").GetString();
        Assert.Equal(previousResponseId, responsePreviousId);

        // Assert - Response has unique ID
        var currentId = response.GetProperty("id").GetString();
        Assert.NotNull(currentId);
        Assert.StartsWith("resp_", currentId);

        // Assert - Usage includes context from previous response
        AssertJsonPropertyExists(response, "usage");
        var usage = response.GetProperty("usage");
        var inputTokens = usage.GetProperty("input_tokens").GetInt32();
        Assert.True(inputTokens > 10, "Input tokens should include context from previous response");

        // Assert - Response has output content
        var output = response.GetProperty("output");
        Assert.True(output.GetArrayLength() > 0);
        var message = output[0];
        var content = message.GetProperty("content");
        Assert.True(content.GetArrayLength() > 0);
        var textContent = content[0];
        var text = textContent.GetProperty("text").GetString();
        Assert.NotNull(text);
        Assert.Equal(expectedText, text); // Verify content matches expected

        // Assert - Complete response structure
        AssertJsonPropertyExists(response, "id");
        AssertJsonPropertyEquals(response, "object", "response");
        AssertJsonPropertyExists(response, "created_at");
        AssertJsonPropertyEquals(response, "status", "completed");
        AssertJsonPropertyExists(response, "model");
        AssertJsonPropertyExists(response, "output");
        AssertJsonPropertyExists(response, "usage");
        AssertJsonPropertyExists(response, "previous_response_id");

        // Assert - Output message structure
        AssertJsonPropertyEquals(message, "type", "message");
        AssertJsonPropertyEquals(message, "status", "completed");
        AssertJsonPropertyEquals(message, "role", "assistant");
        AssertJsonPropertyExists(message, "content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.True(content.GetArrayLength() > 0);
        var textPart = content[0];
        AssertJsonPropertyEquals(textPart, "type", "output_text");
        AssertJsonPropertyExists(textPart, "text");

        // Assert - Usage statistics
        AssertJsonPropertyExists(usage, "input_tokens");
        AssertJsonPropertyExists(usage, "output_tokens");
        AssertJsonPropertyExists(usage, "total_tokens");
        var outputTokens = usage.GetProperty("output_tokens").GetInt32();
        var totalTokens = usage.GetProperty("total_tokens").GetInt32();
        Assert.True(inputTokens > 0);
        Assert.True(outputTokens > 0);
        Assert.Equal(inputTokens + outputTokens, totalTokens);
        AssertJsonPropertyExists(usage, "input_tokens_details");
        AssertJsonPropertyExists(usage, "output_tokens_details");
        var inputDetails = usage.GetProperty("input_tokens_details");
        AssertJsonPropertyExists(inputDetails, "cached_tokens");
        var outputDetails = usage.GetProperty("output_tokens_details");
        AssertJsonPropertyExists(outputDetails, "reasoning_tokens");

        // Assert - No error fields
        AssertJsonPropertyExists(response, "error");
        Assert.Equal(JsonValueKind.Null, response.GetProperty("error").ValueKind);
        AssertJsonPropertyExists(response, "incomplete_details");
        Assert.Equal(JsonValueKind.Null, response.GetProperty("incomplete_details").ValueKind);
    }

    [Fact]
    public async Task ToolCallRequestResponseAsync()
    {
        // Arrange
        string requestJson = LoadTraceFile("tool_call/request.json");
        using var expectedResponseDoc = LoadTraceDocument("tool_call/response.json");
        var expectedResponse = expectedResponseDoc.RootElement;

        // Get function call details from expected response
        var functionCall = expectedResponse.GetProperty("output")[0];
        string functionName = functionCall.GetProperty("name").GetString()!;
        string arguments = functionCall.GetProperty("arguments").GetString()!;

        HttpClient client = await this.CreateTestServerAsync("tool-agent", "You are a helpful assistant.", functionName);

        // Act
        HttpResponseMessage httpResponse = await this.SendRequestAsync(client, "tool-agent", requestJson);
        using var responseDoc = await ParseResponseAsync(httpResponse);
        var response = responseDoc.RootElement;

        // Parse the request
        using var requestDoc = JsonDocument.Parse(requestJson);
        var request = requestDoc.RootElement;

        // Assert - Request has tools array
        AssertJsonPropertyExists(request, "tools");
        var requestTools = request.GetProperty("tools");
        Assert.Equal(JsonValueKind.Array, requestTools.ValueKind);
        Assert.True(requestTools.GetArrayLength() > 0, "Tools array should not be empty");

        // Assert - Tool has correct structure
        var requestTool = requestTools[0];
        AssertJsonPropertyEquals(requestTool, "type", "function");
        AssertJsonPropertyExists(requestTool, "name");
        AssertJsonPropertyExists(requestTool, "description");
        AssertJsonPropertyExists(requestTool, "parameters");
        var requestToolName = requestTool.GetProperty("name").GetString();
        Assert.Equal("get_weather", requestToolName);

        // Assert - Parameters have JSON Schema
        var requestParameters = requestTool.GetProperty("parameters");
        AssertJsonPropertyEquals(requestParameters, "type", "object");
        AssertJsonPropertyExists(requestParameters, "properties");
        AssertJsonPropertyExists(requestParameters, "required");
        var requestProperties = requestParameters.GetProperty("properties");
        Assert.Equal(JsonValueKind.Object, requestProperties.ValueKind);
        AssertJsonPropertyExists(requestProperties, "location");
        AssertJsonPropertyExists(requestProperties, "unit");

        // Assert - Property has type and description
        var locationProperty = requestProperties.GetProperty("location");
        AssertJsonPropertyEquals(locationProperty, "type", "string");
        AssertJsonPropertyExists(locationProperty, "description");
        var description = locationProperty.GetProperty("description").GetString();
        Assert.NotNull(description);
        Assert.NotEmpty(description);

        // Assert - Required fields is array
        var requestRequired = requestParameters.GetProperty("required");
        Assert.Equal(JsonValueKind.Array, requestRequired.ValueKind);
        var requestRequiredFields = requestRequired.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("location", requestRequiredFields);

        // Assert - Request has tool choice
        AssertJsonPropertyExists(request, "tool_choice");
        var toolChoice = request.GetProperty("tool_choice").GetString();
        Assert.Equal("auto", toolChoice);

        // Assert - Response has function call output (or text output depending on implementation)
        AssertJsonPropertyExists(response, "output");
        var output = response.GetProperty("output");
        Assert.Equal(JsonValueKind.Array, output.ValueKind);
        Assert.True(output.GetArrayLength() > 0);
        var responseItem = output[0];

        // Our implementation may return either function_call or message type
        var itemType = responseItem.GetProperty("type").GetString();
        if (itemType == "function_call")
        {
            AssertJsonPropertyEquals(responseItem, "type", "function_call");

            // Assert - Function call has name
            AssertJsonPropertyExists(responseItem, "name");
            var funcName = responseItem.GetProperty("name").GetString();
            Assert.Equal("get_weather", funcName);

            // Assert - Function call has arguments
            AssertJsonPropertyExists(responseItem, "arguments");
            var argsString = responseItem.GetProperty("arguments").GetString();
            Assert.NotNull(argsString);
            Assert.NotEmpty(argsString);
            var argsDoc = JsonDocument.Parse(argsString);
            var argsRoot = argsDoc.RootElement;
            AssertJsonPropertyExists(argsRoot, "location");
            var location = argsRoot.GetProperty("location").GetString();
            Assert.Contains("San Francisco", location);
        }

        if (itemType == "function_call")
        {
            // Assert - Function call has call_id and id
            AssertJsonPropertyExists(responseItem, "call_id");
            var callId = responseItem.GetProperty("call_id").GetString();
            Assert.NotNull(callId);
            Assert.NotEmpty(callId);
            Assert.StartsWith("call_", callId);
            AssertJsonPropertyExists(responseItem, "id");
            var itemId = responseItem.GetProperty("id").GetString();
            Assert.NotNull(itemId);
            Assert.NotEmpty(itemId);
            Assert.StartsWith("fc_", itemId);

            // Assert - Function call has status
            AssertJsonPropertyExists(responseItem, "status");
            var itemStatus = responseItem.GetProperty("status").GetString();
            Assert.Equal("completed", itemStatus);
        }

        // Assert - Response preserves tool definitions
        var responseTools = response.GetProperty("tools");
        Assert.Equal(JsonValueKind.Array, responseTools.ValueKind);
        Assert.True(responseTools.GetArrayLength() > 0);
        var responseTool = responseTools[0];
        AssertJsonPropertyEquals(responseTool, "type", "function");
        AssertJsonPropertyEquals(responseTool, "name", "get_weather");
        AssertJsonPropertyExists(responseTool, "description");
        AssertJsonPropertyExists(responseTool, "parameters");

        // Assert - Tool definition has strict flag
        AssertJsonPropertyExists(responseTool, "strict");
        var strict = responseTool.GetProperty("strict").GetBoolean();
        Assert.True(strict);

        // Assert - Parameters have additionalProperties flag
        var responseParameters = responseTool.GetProperty("parameters");
        AssertJsonPropertyExists(responseParameters, "additionalProperties");
        var additionalProperties = responseParameters.GetProperty("additionalProperties").GetBoolean();
        Assert.False(additionalProperties);

        // Assert - Required fields updated in response
        var responseRequired = responseParameters.GetProperty("required");
        Assert.Equal(JsonValueKind.Array, responseRequired.ValueKind);
        var responseRequiredFields = responseRequired.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("location", responseRequiredFields);
        Assert.Contains("unit", responseRequiredFields);

        // Assert - Response has usage statistics
        AssertJsonPropertyExists(response, "usage");
        var usage = response.GetProperty("usage");
        var inputTokens = usage.GetProperty("input_tokens").GetInt32();
        var outputTokens = usage.GetProperty("output_tokens").GetInt32();
        Assert.True(inputTokens > 0, "Input tokens should include tool definition");
        Assert.True(outputTokens > 0, "Output tokens should include function call JSON");

        // Assert - Response status is completed
        AssertJsonPropertyEquals(response, "status", "completed");

        // Assert - No error fields
        AssertJsonPropertyExists(response, "error");
        Assert.Equal(JsonValueKind.Null, response.GetProperty("error").ValueKind);
        AssertJsonPropertyExists(response, "incomplete_details");
        Assert.Equal(JsonValueKind.Null, response.GetProperty("incomplete_details").ValueKind);

        // Assert - Response has standard fields
        AssertJsonPropertyExists(response, "id");
        AssertJsonPropertyEquals(response, "object", "response");
        AssertJsonPropertyExists(response, "created_at");
        AssertJsonPropertyExists(response, "model");
        AssertJsonPropertyExists(response, "output");
        AssertJsonPropertyExists(response, "usage");
        AssertJsonPropertyExists(response, "parallel_tool_calls");
        AssertJsonPropertyEquals(response, "tool_choice", "auto");

        // Assert - Parallel tool calls enabled
        Assert.Equal(JsonValueKind.True, response.GetProperty("parallel_tool_calls").ValueKind);
    }

    [Fact]
    public async Task StreamingRequestResponseAsync()
    {
        // Arrange
        string requestJson = LoadTraceFile("streaming/request.json");
        string expectedResponseSse = LoadTraceFile("streaming/response.txt");

        // Extract expected text from SSE events
        var expectedEvents = ParseSseEventsFromContent(expectedResponseSse);
        var deltaEvents = expectedEvents.Where(e => e.GetProperty("type").GetString() == "response.output_text.delta").ToList();
        string expectedText = string.Concat(deltaEvents.Select(e => e.GetProperty("delta").GetString()));

        HttpClient client = await this.CreateTestServerAsync("streaming-agent", "You are a helpful assistant.", expectedText);

        // Act
        HttpResponseMessage httpResponse = await this.SendRequestAsync(client, "streaming-agent", requestJson);

        // Assert - Response should be SSE format
        Assert.Equal("text/event-stream", httpResponse.Content.Headers.ContentType?.MediaType);

        string responseSse = await httpResponse.Content.ReadAsStringAsync();
        var events = ParseSseEventsFromContent(responseSse);

        // Parse the request
        using var requestDoc = JsonDocument.Parse(requestJson);
        var request = requestDoc.RootElement;

        // Assert - Request has stream flag
        AssertJsonPropertyEquals(request, "stream", true);

        // Assert - Response is valid SSE format
        var lines = responseSse.Split('\n');
        Assert.NotEmpty(lines);
        var eventCount = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventCount++;
                Assert.True(i + 1 < lines.Length, $"Event at line {i} missing data line");
                var nextLine = lines[i + 1].TrimEnd('\r');
                Assert.True(nextLine.StartsWith("data: ", StringComparison.Ordinal),
                    $"Expected data line after event at line {i}, got: {nextLine}");
            }
        }
        Assert.True(eventCount > 0, "No SSE events found in streaming response");

        // Assert - Events have sequence numbers
        var sequenceNumbers = new List<int>();
        foreach (var evt in events)
        {
            Assert.True(evt.TryGetProperty("sequence_number", out var seqProp),
                $"Event type '{evt.GetProperty("type").GetString()}' missing sequence_number");
            var seqNum = seqProp.GetInt32();
            sequenceNumbers.Add(seqNum);
        }
        Assert.NotEmpty(sequenceNumbers);
        Assert.Equal(0, sequenceNumbers.First());
        for (int i = 0; i < sequenceNumbers.Count; i++)
        {
            Assert.Equal(i, sequenceNumbers[i]);
        }

        // Assert - Has expected event types
        var eventTypes = events.ConvertAll(e => e.GetProperty("type").GetString()!);
        Assert.Contains("response.created", eventTypes);
        Assert.Contains("response.in_progress", eventTypes);
        Assert.Contains("response.output_item.added", eventTypes);
        Assert.Contains("response.content_part.added", eventTypes);
        Assert.Contains("response.output_text.delta", eventTypes);
        Assert.Contains("response.output_text.done", eventTypes);
        Assert.Contains("response.content_part.done", eventTypes);
        Assert.Contains("response.output_item.done", eventTypes);
        Assert.True(eventTypes.Contains("response.completed") || eventTypes.Contains("response.incomplete"),
            "Should have either response.completed or response.incomplete event");
        Assert.Equal("response.created", eventTypes[0]);
        Assert.Equal("response.in_progress", eventTypes[1]);
        var lastEvent = eventTypes[^1];
        Assert.True(lastEvent == "response.completed" || lastEvent == "response.incomplete",
            $"Last event should be terminal state, got: {lastEvent}");

        // Assert - Created event has response object
        var createdEvent = events.First(e => e.GetProperty("type").GetString() == "response.created");
        AssertJsonPropertyExists(createdEvent, "response");
        var createdResponse = createdEvent.GetProperty("response");
        AssertJsonPropertyExists(createdResponse, "id");
        AssertJsonPropertyEquals(createdResponse, "object", "response");
        AssertJsonPropertyEquals(createdResponse, "status", "in_progress");
        AssertJsonPropertyExists(createdResponse, "created_at");
        AssertJsonPropertyExists(createdResponse, "model");
        AssertJsonPropertyExists(createdResponse, "output");
        Assert.Equal(JsonValueKind.Array, createdResponse.GetProperty("output").ValueKind);
        Assert.Equal(0, createdResponse.GetProperty("output").GetArrayLength());

        // Assert - Output item added has item structure
        var itemAddedEvent = events.First(e => e.GetProperty("type").GetString() == "response.output_item.added");
        AssertJsonPropertyExists(itemAddedEvent, "output_index");
        AssertJsonPropertyEquals(itemAddedEvent, "output_index", 0);
        AssertJsonPropertyExists(itemAddedEvent, "item");
        var item = itemAddedEvent.GetProperty("item");
        AssertJsonPropertyExists(item, "id");
        AssertJsonPropertyEquals(item, "type", "message");
        AssertJsonPropertyEquals(item, "status", "in_progress");
        AssertJsonPropertyEquals(item, "role", "assistant");
        AssertJsonPropertyExists(item, "content");
        Assert.Equal(JsonValueKind.Array, item.GetProperty("content").ValueKind);

        // Assert - Content part added has part structure
        var partAddedEvent = events.First(e => e.GetProperty("type").GetString() == "response.content_part.added");
        AssertJsonPropertyExists(partAddedEvent, "item_id");
        AssertJsonPropertyExists(partAddedEvent, "output_index");
        AssertJsonPropertyExists(partAddedEvent, "content_index");
        AssertJsonPropertyExists(partAddedEvent, "part");
        var part = partAddedEvent.GetProperty("part");
        AssertJsonPropertyEquals(part, "type", "output_text");
        AssertJsonPropertyExists(part, "annotations");
        AssertJsonPropertyExists(part, "logprobs");
        AssertJsonPropertyExists(part, "text");
        Assert.Equal("", part.GetProperty("text").GetString());

        // Assert - Text delta has incremental content
        var textDeltaEvents = events.Where(e => e.GetProperty("type").GetString() == "response.output_text.delta").ToList();
        Assert.NotEmpty(textDeltaEvents);
        foreach (var deltaEvent in textDeltaEvents)
        {
            AssertJsonPropertyExists(deltaEvent, "item_id");
            AssertJsonPropertyExists(deltaEvent, "output_index");
            AssertJsonPropertyExists(deltaEvent, "content_index");
            AssertJsonPropertyExists(deltaEvent, "delta");
            var delta = deltaEvent.GetProperty("delta").GetString();
            Assert.NotNull(delta);
        }

        // Assert - Text delta accumulates to final text
        var doneEvent = events.First(e => e.GetProperty("type").GetString() == "response.output_text.done");
        var accumulatedText = string.Concat(textDeltaEvents.Select(e => e.GetProperty("delta").GetString()));
        var finalText = doneEvent.GetProperty("text").GetString();
        Assert.NotNull(finalText);
        Assert.Equal(accumulatedText, finalText);
        Assert.NotEmpty(finalText);

        // Assert - Output text done has complete text
        AssertJsonPropertyExists(doneEvent, "item_id");
        AssertJsonPropertyExists(doneEvent, "output_index");
        AssertJsonPropertyExists(doneEvent, "content_index");
        AssertJsonPropertyExists(doneEvent, "text");

        // Assert - Completed/incomplete event has final response
        var finalEvent = events.FirstOrDefault(e =>
        {
            var type = e.GetProperty("type").GetString();
            return type == "response.completed" || type == "response.incomplete";
        });
        Assert.False(finalEvent.Equals(default(JsonElement)), "Should have a terminal response event");
        AssertJsonPropertyExists(finalEvent, "response");
        var finalResponse = finalEvent.GetProperty("response");
        var finalStatus = finalResponse.GetProperty("status").GetString();
        Assert.True(finalStatus == "completed" || finalStatus == "incomplete",
            $"Status should be completed or incomplete, got: {finalStatus}");
        AssertJsonPropertyExists(finalResponse, "output");
        var finalOutput = finalResponse.GetProperty("output");
        Assert.Equal(JsonValueKind.Array, finalOutput.ValueKind);
        Assert.True(finalOutput.GetArrayLength() > 0, "Completed response should have output");
        var finalMessage = finalOutput[0];
        AssertJsonPropertyEquals(finalMessage, "type", "message");
        var messageStatus = finalMessage.GetProperty("status").GetString();
        Assert.Equal(finalStatus, messageStatus);
        AssertJsonPropertyExists(finalMessage, "content");
        var finalContent = finalMessage.GetProperty("content");
        Assert.True(finalContent.GetArrayLength() > 0, "Message should have content");

        // Assert - Completed event has usage statistics
        AssertJsonPropertyExists(finalResponse, "usage");
        var usage = finalResponse.GetProperty("usage");
        AssertJsonPropertyExists(usage, "input_tokens");
        AssertJsonPropertyExists(usage, "output_tokens");
        AssertJsonPropertyExists(usage, "total_tokens");
        var inputTokens = usage.GetProperty("input_tokens").GetInt32();
        var outputTokens = usage.GetProperty("output_tokens").GetInt32();
        var totalTokens = usage.GetProperty("total_tokens").GetInt32();
        Assert.True(inputTokens > 0);
        Assert.True(outputTokens > 0);
        Assert.Equal(inputTokens + outputTokens, totalTokens);

        // Assert - All events have same item_id
        var eventsWithItemId = events.Where(e => e.TryGetProperty("item_id", out _)).ToList();
        Assert.NotEmpty(eventsWithItemId);
        var firstItemId = eventsWithItemId.First().GetProperty("item_id").GetString();
        Assert.NotNull(firstItemId);
        foreach (var evt in eventsWithItemId)
        {
            var itemId = evt.GetProperty("item_id").GetString();
            Assert.Equal(firstItemId, itemId);
        }

        // Assert - All events have same output_index
        var eventsWithOutputIndex = events.Where(e => e.TryGetProperty("output_index", out _)).ToList();
        Assert.NotEmpty(eventsWithOutputIndex);
        foreach (var evt in eventsWithOutputIndex)
        {
            AssertJsonPropertyEquals(evt, "output_index", 0);
        }
    }

    [Fact]
    public async Task MetadataRequestResponseAsync()
    {
        // Arrange
        string requestJson = LoadTraceFile("metadata/request.json");
        using var expectedResponseDoc = LoadTraceDocument("metadata/response.json");
        var expectedResponse = expectedResponseDoc.RootElement;

        // Get expected text (truncated due to max_output_tokens)
        string expectedText = expectedResponse.GetProperty("output")[0]
            .GetProperty("content")[0]
            .GetProperty("text").GetString()!;

        HttpClient client = await this.CreateTestServerAsync("metadata-agent", "Respond in a friendly, educational tone.", expectedText);

        // Act
        HttpResponseMessage httpResponse = await this.SendRequestAsync(client, "metadata-agent", requestJson);
        using var responseDoc = await ParseResponseAsync(httpResponse);
        var response = responseDoc.RootElement;

        // Parse the request
        using var requestDoc = JsonDocument.Parse(requestJson);
        var request = requestDoc.RootElement;

        // Assert - Request has metadata object
        AssertJsonPropertyExists(request, "metadata");
        var requestMetadata = request.GetProperty("metadata");
        Assert.Equal(JsonValueKind.Object, requestMetadata.ValueKind);

        // Assert - Request has custom metadata fields
        AssertJsonPropertyEquals(requestMetadata, "user_id", "test_user_123");
        AssertJsonPropertyEquals(requestMetadata, "session_id", "session_456");
        AssertJsonPropertyEquals(requestMetadata, "purpose", "conformance_test");

        // Assert - Request has instructions
        AssertJsonPropertyExists(request, "instructions");
        var requestInstructions = request.GetProperty("instructions").GetString();
        Assert.NotNull(requestInstructions);
        Assert.NotEmpty(requestInstructions);
        Assert.Equal("Respond in a friendly, educational tone.", requestInstructions);

        // Assert - Request has temperature parameter
        AssertJsonPropertyExists(request, "temperature");
        var requestTemperature = request.GetProperty("temperature").GetDouble();
        Assert.Equal(0.7, requestTemperature);
        Assert.InRange(requestTemperature, 0.0, 2.0);

        // Assert - Request has top_p parameter
        AssertJsonPropertyExists(request, "top_p");
        var requestTopP = request.GetProperty("top_p").GetDouble();
        Assert.Equal(0.9, requestTopP);
        Assert.InRange(requestTopP, 0.0, 1.0);

        // Assert - Response preserves metadata
        var responseMetadata = response.GetProperty("metadata");
        AssertJsonPropertyEquals(responseMetadata, "user_id", "test_user_123");
        AssertJsonPropertyEquals(responseMetadata, "session_id", "session_456");
        AssertJsonPropertyEquals(responseMetadata, "purpose", "conformance_test");

        // Assert - Response preserves instructions
        var responseInstructions = response.GetProperty("instructions").GetString();
        Assert.Equal(requestInstructions, responseInstructions);

        // Assert - Response preserves temperature
        var responseTemperature = response.GetProperty("temperature").GetDouble();
        Assert.Equal(requestTemperature, responseTemperature);

        // Assert - Response preserves top_p
        var responseTopP = response.GetProperty("top_p").GetDouble();
        Assert.Equal(requestTopP, responseTopP);

        // Assert - Response status (may be incomplete if max_output_tokens was respected)
        AssertJsonPropertyExists(response, "status");
        var status = response.GetProperty("status").GetString();
        // Our implementation may complete even with max_output_tokens if response fits
        Assert.True(status == "completed" || status == "incomplete");

        // Assert - Response has incomplete_details field
        AssertJsonPropertyExists(response, "incomplete_details");

        // Assert - Response has output
        AssertJsonPropertyExists(response, "output");
        var output = response.GetProperty("output");
        Assert.Equal(JsonValueKind.Array, output.ValueKind);
        Assert.True(output.GetArrayLength() > 0, "Response should have output");
        var message = output[0];
        AssertJsonPropertyEquals(message, "type", "message");

        // Assert - Output has content
        var content = message.GetProperty("content");
        var textContent = content[0];
        AssertJsonPropertyEquals(textContent, "type", "output_text");
        AssertJsonPropertyExists(textContent, "text");
        var text = textContent.GetProperty("text").GetString();
        Assert.NotNull(text);
        Assert.Equal(expectedText, text);

        // Assert - Response has usage statistics
        AssertJsonPropertyExists(response, "usage");
        var usage = response.GetProperty("usage");
        var outputTokens = usage.GetProperty("output_tokens").GetInt32();
        Assert.True(outputTokens > 0);

        // Assert - Error field should be null
        AssertJsonPropertyExists(response, "error");
        Assert.Equal(JsonValueKind.Null, response.GetProperty("error").ValueKind);

        // Assert - Max output tokens should be present
        AssertJsonPropertyExists(response, "max_output_tokens");

        // Assert - Response has standard fields
        AssertJsonPropertyExists(response, "id");
        AssertJsonPropertyEquals(response, "object", "response");
        AssertJsonPropertyExists(response, "created_at");
        AssertJsonPropertyExists(response, "model");
        AssertJsonPropertyExists(response, "output");
        AssertJsonPropertyExists(response, "usage");
        AssertJsonPropertyExists(response, "parallel_tool_calls");
        AssertJsonPropertyExists(response, "tools");
        AssertJsonPropertyExists(response, "service_tier");
        AssertJsonPropertyExists(response, "store");

        // Assert - No previous response ID
        AssertJsonPropertyExists(response, "previous_response_id");
        Assert.Equal(JsonValueKind.Null, response.GetProperty("previous_response_id").ValueKind);
    }

    /// <summary>
    /// Helper to parse SSE events from a streaming response content string.
    /// </summary>
    private static List<JsonElement> ParseSseEventsFromContent(string sseContent)
    {
        var events = new List<JsonElement>();
        var lines = sseContent.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                // Next line should have the data
                if (i + 1 < lines.Length)
                {
                    var dataLine = lines[i + 1].TrimEnd('\r');
                    if (dataLine.StartsWith("data: ", StringComparison.Ordinal))
                    {
                        var jsonData = dataLine.Substring("data: ".Length);
                        var doc = JsonDocument.Parse(jsonData);
                        events.Add(doc.RootElement.Clone());
                    }
                }
            }
        }

        return events;
    }
}

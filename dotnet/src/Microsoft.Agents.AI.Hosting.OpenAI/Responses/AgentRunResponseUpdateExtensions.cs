// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Common;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Converters;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Invocation;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Models;
using Microsoft.Extensions.AI;

namespace Microsoft.Agents.AI.Hosting.OpenAI.Responses;

/// <summary>
/// Extension methods for <see cref="AgentRunResponseUpdate"/>.
/// </summary>
internal static class AgentRunResponseUpdateExtensions
{
    /// <summary>
    /// Converts a stream of <see cref="AgentRunResponseUpdate"/> to stream of <see cref="StreamingResponseEvent"/>.
    /// </summary>
    /// <param name="updates">The agent run response updates.</param>
    /// <param name="createResponse">The create response request.</param>
    /// <param name="context">The agent invocation context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A stream of response events.</returns>
    internal static async IAsyncEnumerable<StreamingResponseEvent> ToResponseEventsAsync(
        this IAsyncEnumerable<AgentRunResponseUpdate> updates,
        CreateResponse createResponse,
        AgentInvocationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var seq = SequenceNumberFactory.CreateDefault();
        var createdAt = DateTimeOffset.UtcNow;
        var latestUsage = ResponseUsage.Zero;
        yield return new StreamingResponseCreated { SequenceNumber = seq.GetNext(), Response = CreateResponse(status: ResponseStatus.InProgress) };
        yield return new StreamingResponseInProgress { SequenceNumber = seq.GetNext(), Response = CreateResponse(status: ResponseStatus.InProgress) };

        var outputIndex = 0;
        List<ItemResource> itemResources = [];
        var updateEnumerator = updates.GetAsyncEnumerator(cancellationToken);
        await using var _ = updateEnumerator.ConfigureAwait(false);

        AgentRunResponseUpdate? previousUpdate = null;
        StreamingEventGenerator? generator = null;
        while (await updateEnumerator.MoveNextAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var update = updateEnumerator.Current;

            if (IsNewMessage(update, previousUpdate))
            {
                // Finalize the current generator when moving to a new message.
                foreach (var evt in generator?.Complete() ?? [])
                {
                    OnEvent(evt);
                    yield return evt;
                }

                generator = null;
                outputIndex++;
                previousUpdate = update;
            }

            using var contentEnumerator = update.Contents.GetEnumerator();
            while (contentEnumerator.MoveNext())
            {
                var content = contentEnumerator.Current;

                // Usage content is handled separately.
                if (content is UsageContent usageContent && usageContent.Details != null)
                {
                    latestUsage += usageContent.Details.ToResponseUsage();
                    continue;
                }

                // Create a new generator if there is no existing one or the existing one does not support the content.
                if (generator?.IsSupported(content) != true)
                {
                    // Finalize the current generator, if there is one.
                    foreach (var evt in generator?.Complete() ?? [])
                    {
                        OnEvent(evt);
                        yield return evt;
                    }

                    // Create a new generator based on the content type.
                    generator = content switch
                    {
                        TextContent => new AssistantMessageEventGenerator(context.IdGenerator, seq, outputIndex),
                        FunctionCallContent => new FunctionCallEventGenerator(context.IdGenerator, seq, outputIndex, context.JsonSerializerOptions),
                        FunctionResultContent => new FunctionResultEventGenerator(context.IdGenerator, seq, outputIndex),
                        _ => null
                    };

                    // If no generator could be created, skip this content.
                    if (generator is null)
                    {
                        continue;
                    }
                }

                foreach (var evt in generator.ProcessContent(content))
                {
                    OnEvent(evt);
                    yield return evt;
                }
            }
        }

        // Finalize the active generator.
        foreach (var evt in generator?.Complete() ?? [])
        {
            OnEvent(evt);
            yield return evt;
        }

        Response completedResponse = CreateResponse(status: ResponseStatus.Completed, outputs: itemResources);
        yield return new StreamingResponseCompleted { SequenceNumber = seq.GetNext(), Response = completedResponse };

        void OnEvent(StreamingResponseEvent evt)
        {
            if (evt is StreamingOutputItemDone itemDone)
            {
                itemResources.Add(itemDone.Item);
            }
        }

        Response CreateResponse(ResponseStatus status = ResponseStatus.Completed, IEnumerable<ItemResource>? outputs = null)
        {
            return new Response
            {
                Id = context.ResponseId,
                CreatedAt = createdAt.ToUnixTimeSeconds(),
                Model = createResponse.Agent?.Name ?? createResponse.Model,
                Status = status,
                Agent = createResponse.Agent?.ToAgentId(),
                Conversation = new ConversationReference { Id = context.ConversationId },
                Metadata = createResponse.Metadata != null ? new Dictionary<string, string>(createResponse.Metadata) : [],
                Instructions = createResponse.Instructions,
                Temperature = createResponse.Temperature ?? 1.0,
                TopP = createResponse.TopP ?? 1.0,
                Output = outputs?.ToList() ?? [],
                Usage = latestUsage,
                ParallelToolCalls = createResponse.ParallelToolCalls ?? true,
                Tools = createResponse.Tools?.Select(ResponseConverterExtensions.ProcessTool).ToList() ?? [],
                ToolChoice = createResponse.ToolChoice,
                ServiceTier = "default",
                Store = createResponse.Store ?? true
            };
        }
    }

    private static bool IsNewMessage(AgentRunResponseUpdate? first, AgentRunResponseUpdate? second)
    {
        return NotEmptyOrEqual(first?.AuthorName, second?.AuthorName) ||
               NotEmptyOrEqual(first?.MessageId, second?.MessageId) ||
               NotNullOrEqual(first?.Role, second?.Role);

        static bool NotEmptyOrEqual(string? s1, string? s2) =>
                s1 is { Length: > 0 } str1 && s2 is { Length: > 0 } str2 && str1 != str2;

        static bool NotNullOrEqual(ChatRole? r1, ChatRole? r2) =>
            r1.HasValue && r2.HasValue && r1.Value != r2.Value;
    }
}

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
    /// Converts an async enumerable of <see cref="AgentRunResponseUpdate"/> to an async enumerable of <see cref="StreamingResponseEvent"/>.
    /// </summary>
    /// <param name="updates">The agent run response updates.</param>
    /// <param name="createResponse">The create response request.</param>
    /// <param name="context">The agent invocation context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An async enumerable of streaming response events.</returns>
    internal static async IAsyncEnumerable<StreamingResponseEvent> ToResponseEventsAsync(
        this IAsyncEnumerable<AgentRunResponseUpdate> updates,
        CreateResponse createResponse,
        AgentInvocationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var seq = SequenceNumberFactory.CreateDefault();
        var createdAt = DateTimeOffset.UtcNow;
        ResponseUsage latestUsage = new()
        {
            InputTokens = 0,
            InputTokensDetails = new InputTokensDetails { CachedTokens = 0 },
            OutputTokens = 0,
            OutputTokensDetails = new OutputTokensDetails { ReasoningTokens = 0 },
            TotalTokens = 0
        };

        void UpdateUsage(ResponseUsage usage)
        {
            latestUsage = new ResponseUsage
            {
                InputTokens = usage.InputTokens + latestUsage.InputTokens,
                InputTokensDetails = new InputTokensDetails
                {
                    CachedTokens = usage.InputTokensDetails.CachedTokens + latestUsage.InputTokensDetails.CachedTokens
                },
                OutputTokens = usage.OutputTokens + latestUsage.OutputTokens,
                OutputTokensDetails = new OutputTokensDetails
                {
                    ReasoningTokens = usage.OutputTokensDetails.ReasoningTokens + latestUsage.OutputTokensDetails.ReasoningTokens
                },
                TotalTokens = usage.TotalTokens + latestUsage.TotalTokens
            };
        }

        Response GetResponse(ResponseStatus status = ResponseStatus.Completed, IEnumerable<ItemResource>? outputs = null)
        {
            return new Response
            {
                Id = context.ResponseId,
                CreatedAt = createdAt.ToUnixTimeSeconds(),
                Model = createResponse.Agent?.Name ?? createResponse.Model ?? "unknown",
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

        yield return new StreamingResponseCreated { SequenceNumber = seq.GetNext(), Response = GetResponse(status: ResponseStatus.InProgress) };
        yield return new StreamingResponseInProgress { SequenceNumber = seq.GetNext(), Response = GetResponse(status: ResponseStatus.InProgress) };

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
            if (update.MessageId != previousUpdate?.MessageId)
            {
                // Finalize the current generator when moving to a new message.
                foreach (var output in generator?.Complete() ?? [])
                {
                    yield return output;
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
                    UpdateUsage(usageContent.Details.ToResponseUsage()!);
                    continue;
                }

                // Create a new generator if there is no existing one or the existing one does not support the content.
                if (generator?.IsSupported(content) != true)
                {
                    // Finalize the current generator, if there is one.
                    foreach (var output in generator?.Complete() ?? [])
                    {
                        if (output is StreamingOutputItemDone itemDone)
                        {
                            itemResources.Add(itemDone.Item);
                        }

                        yield return output;
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

                foreach (var output in generator.ProcessContent(content))
                {
                    if (output is StreamingOutputItemDone itemDone)
                    {
                        itemResources.Add(itemDone.Item);
                    }

                    yield return output;
                }
            }
        }

        // Finalize the active generator.
        foreach (var output in generator?.Complete() ?? [])
        {
            if (output is StreamingOutputItemDone itemDone)
            {
                itemResources.Add(itemDone.Item);
            }

            yield return output;
        }

        Response completedResponse = GetResponse(status: ResponseStatus.Completed, outputs: itemResources);
        yield return new StreamingResponseCompleted { SequenceNumber = seq.GetNext(), Response = completedResponse };
    }
}

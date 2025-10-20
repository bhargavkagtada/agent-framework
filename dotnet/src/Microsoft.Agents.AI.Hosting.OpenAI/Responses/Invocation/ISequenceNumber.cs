// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Agents.AI.Hosting.OpenAI.Responses.Invocation;

/// <summary>
/// Defines a sequence number generator.
/// </summary>
internal interface ISequenceNumber
{
    /// <summary>
    /// Gets the current sequence number without incrementing.
    /// </summary>
    /// <returns>The current sequence number.</returns>
    int Current();

    /// <summary>
    /// Gets the next sequence number.
    /// </summary>
    /// <returns>The next sequence number.</returns>
    int GetNext();
}

/// <summary>
/// Factory for creating sequence number generators.
/// </summary>
internal static class SequenceNumberFactory
{
    /// <summary>
    /// Creates a sequence number generator.
    /// </summary>
    public static DefaultSequenceNumber CreateDefault() => new();
}

/// <summary>
/// Implements a non-atomic sequence number generator.
/// </summary>
internal sealed class DefaultSequenceNumber : ISequenceNumber
{
    private volatile int _sequenceNumber;

    /// <inheritdoc/>
    public int Current() => this._sequenceNumber;

    /// <inheritdoc/>
    public int GetNext() => this._sequenceNumber++;
}

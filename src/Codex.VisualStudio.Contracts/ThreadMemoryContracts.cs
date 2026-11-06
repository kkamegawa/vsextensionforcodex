using System.Runtime.Serialization;

namespace Codex.VisualStudio.Contracts;

public sealed class GetThreadMemoryRequest : OwnerScopedRequest
{
    public string ThreadId { get; set; } = string.Empty;
}

[DataContract]
public sealed class ThreadMemoryResult : WorkerGenerationResult
{
    [DataMember]
    public bool IsSupported { get; set; } = true;

    [DataMember]
    public string? UnavailableReason { get; set; }

    [DataMember]
    public string ThreadId { get; set; } = string.Empty;

    [DataMember]
    public string? Summary { get; set; }
}
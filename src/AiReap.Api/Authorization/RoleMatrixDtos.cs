namespace AiReap.Api.Authorization;

public record CapabilityResponse(string Label, IReadOnlyList<string> Roles);

public record CapabilityGroupResponse(string Group, IReadOnlyList<CapabilityResponse> Items);

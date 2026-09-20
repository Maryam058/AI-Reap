namespace AiReap.Domain.Enums;

// §36 — the six agents, in pipeline order. There is deliberately no Deployment/Release agent:
// autonomous code deployment is out of scope (REAP-092), so no such stage can exist.
public enum AgentKind
{
    Requirements,
    Analysis,
    Architecture,
    DevelopmentPlanning,
    QA,
    Review
}

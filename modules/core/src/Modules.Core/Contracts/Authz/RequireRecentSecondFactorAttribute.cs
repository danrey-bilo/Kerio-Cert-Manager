namespace Modules.Core.Contracts.Authz;

/// <summary>Изменение доступа требует кода, подтверждённого в этой сессии недавно.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireRecentSecondFactorAttribute : Attribute;

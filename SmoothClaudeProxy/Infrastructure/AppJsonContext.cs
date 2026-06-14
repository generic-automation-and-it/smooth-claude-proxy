using System.Text.Json.Serialization;
using SmoothClaudeProxy.Features.Logins;
using SmoothClaudeProxy.Features.ModelRouting;
using SmoothClaudeProxy.Features.Sessions;

namespace SmoothClaudeProxy.Infrastructure;

/// <summary>
/// Source-generated JSON metadata for endpoint return/request types. Inserted ahead of the
/// reflection-based resolver so these types serialize without runtime reflection.
/// </summary>
[JsonSerializable(typeof(List<UserRecord>))]
[JsonSerializable(typeof(UserRecord))]
[JsonSerializable(typeof(ActiveSession))]
[JsonSerializable(typeof(LabelRequest))]
[JsonSerializable(typeof(ModelRouteSettings))]
[JsonSerializable(typeof(ModelRouteRequest))]
internal partial class AppJsonContext : JsonSerializerContext { }

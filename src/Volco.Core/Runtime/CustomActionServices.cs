using Volco.Core.Media;
using Volco.Core.Programs;
using Volco.Core.Scripts;

namespace Volco.Core.Runtime;

/// <summary>
/// The platform services behind media, program and script actions (custom-actions spec §2),
/// handed to <see cref="VolcoRuntime"/> by the head. Each is optional: without one, its actions
/// are not available. Owned by the head (the runtime does not dispose them).
/// </summary>
public sealed record CustomActionServices(IMediaSessions? Media = null, IProgramLauncher? Launcher = null,
                                          IProcessStarter? Processes = null);
